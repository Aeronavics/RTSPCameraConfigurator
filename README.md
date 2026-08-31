# Camera Setup

A native Windows (WPF / .NET 9) tool for configuring basic OEM RTSP cameras without
going through their web GUI. It watches the network for cameras, connects on a click,
changes their IP configuration, edits image and encoder settings from a
config-file-driven UI, and keeps a live preview on screen throughout.

Built and verified against a **Vatilon H82** board camera (Hisilicon **Hi3516CV610**,
firmware `V1.16.39-20250721`).

---

## Dependencies

### To build

| | |
|---|---|
| Windows x64 | It is a WPF app; there is no cross-platform build. |
| .NET 9 SDK | `winget install Microsoft.DotNet.SDK.9` |

Nothing else needs installing — NuGet restores the rest on first build.

**NuGet packages**, all pinned in the csproj:

| Package | Why |
|---|---|
| `LibVLCSharp` 3.8.2 | The fallback preview engine. |
| `LibVLCSharp.WPF` 3.8.2 | Its `VideoView`, hosted through `WindowsFormsHost` — which is why `UseWindowsForms` is on in a WPF project. |
| `VideoLAN.LibVLC.Windows` 3.0.21 | Bundles libvlc itself, so **VLC does not have to be installed** on the target machine. It also decodes H.265, which these cameras emit. |

### To run

The publish is self-contained, so the target machine needs **neither .NET nor VLC**.

**ffmpeg is the one thing worth adding.** It is the default preview engine and is
*not* bundled:

```bash
winget install Gyan.FFmpeg
```

It is resolved as `ffmpeg.exe` next to the executable first, then from `PATH`. If it
is missing the app falls back to the libvlc engine automatically (set
`preview.fallBackToVlc` to `false` in `cameras.json` to make its absence an error
instead). The fallback works, but libvlc will not show a live picture on this camera
family below about 300 ms of buffering, which puts a floor under preview latency that
ffmpeg does not have — see **Preview latency** below.

Everything else is just network: the camera has to be on an address this machine can
route to. If it is not, the app can borrow a temporary address for you — see
**Subnets this machine cannot reach**.

---

## Running it

Double-click **`run.cmd`** in the repo root, or run it from a terminal:

```bash
run.cmd
```

It launches the published app, building it first if it has not been built yet. Pass
`/rebuild` to force a fresh publish:

```bash
run.cmd /rebuild
```

`/rebuild` deliberately does **not** delete the publish folder — saved presets live in
`publish\presets`, and wiping it would destroy them. `dotnet publish` overwrites the
build output by itself.

---

## Building

Requires the .NET 9 SDK. `run.cmd` does this for you; the steps below are for when you
want to drive it directly.

```bash
dotnet build "src/RtspCameraSetup/RtspCameraSetup.csproj" -c Release
```

To produce a self-contained build that needs neither .NET nor VLC installed on the
target machine:

```bash
dotnet publish "src/RtspCameraSetup/RtspCameraSetup.csproj" -c Release
```

Copy the whole `bin/Release/net9.0-windows/win-x64/publish/` folder to the target
machine and run `CameraSetup.exe`.

**Deploy the folder, not just the exe.** `PublishSingleFile` is deliberately off:
libvlc loads `libvlc.dll`, `libvlccore.dll` and its 300-odd `plugins/` from
`libvlc\win-x64\` on disk, and the single-file bundler relocates them, so preview
fails at runtime with *"Failed to load required native libraries"*. The app degrades
gracefully if that happens — configuration still works and the preview area explains
why — but the folder layout is the supported one.

`cameras.json` sits next to the executable and is read at startup. Edit it in place —
no rebuild needed.

---

## How the camera was opened up

The stock firmware only documents its web GUI, so the control path was recovered by
reading the pages the camera itself serves. The findings below are what the app
implements; they are recorded here because none of it is vendor-documented.

### Ports

| Port | Service |
|------|---------|
| 80 | `thttpd/2.29` serving the web UI and the `web.cgi` API |
| 554 | RTSP |
| 34567 | XiongMai "Sofia" binary protocol (present, unused by this app) |

Port 34567 is the traditional XM control channel. It is not needed — the CGI API is
simpler, is plain JSON, and exposes everything required.

### The API

Everything goes through `/cgi-bin/web.cgi`.

**Reads** are `GET /cgi-bin/web.cgi?mod=<module>&cmd=get` with a `Session-Id` header.

**Writes** are `POST /cgi-bin/web.cgi` with `Content-Type: application/json`:

```json
{ "mod": "net", "cmd": "set", "param": { ... } }
```

Image writes are the exception — they put the channel in `param` and the payload in
`param2`:

```json
{ "mod": "image", "cmd": "set", "param": { "channel": 0 }, "param2": { ... } }
```

Useful modules:

| Request | Returns |
|---------|---------|
| `mod=net&cmd=get` | `addr`, `netmask`, `gateway`, `dns`, `dhcp_mode`, `mac`, `iface` |
| `mod=image&cmd=get` | ~35 imaging fields — brightness, contrast, saturation, sharpness, exposure, WDR, DRC, mirror/flip, rotation, IR-cut, LED, AWB, anti-flicker |
| `mod=device&cmd=get` | `devtype`, `cpu_type`, `version`, `kernel_version`, `serial_num`, `chn_num` |
| `mod=rtsp&cmd=get` | `enable`, `port`, `auth_enable` |

Modules that reply `param num error` need extra parameters (`mod=video`, `mod=osd`,
`mod=account`); `unknown error happened` means the module does not exist.

### Authentication — three non-obvious quirks

1. **The digest challenge is not sent in a 401.** The `realm` and `nonce` are
   embedded as JavaScript literals in `/view/login.html` and must be scraped from
   the page before authenticating.

2. **The digest response is computed against a fixed URI.** The firmware always
   signs `/cgi-bin/web.cgi?mod=account&cmd=check`, regardless of the endpoint being
   called. Signing the real request URI is rejected. The actual login request goes
   to `mod=session&cmd=login1`.

3. Standard MD5 digest with `qop=auth` otherwise. A successful login returns a
   `Session-Id` response header, presented on every subsequent request. An expired
   session replies `{"status":"expired"}`, which the client handles by
   re-authenticating and retrying once.

### RTSP — the stream password is not the account password

The web UI derives a separate credential and never sends the real password to the
RTSP server:

```
rtsp_password = HMAC-SHA1(key = username, message = md5_hex(account_password))
```

Verified on hardware: `DESCRIBE` with `admin` / `123456` is refused; the same request
with the derived value returns `200 OK`. For `admin` / `123456` the derived password
is `33e97a6101dce4c3ad589754e79b14ad acc52d45` (without the space).

The stream is **H.265/HEVC** by default, which is why preview uses LibVLC rather than
a managed RTSP library — most of those are H.264-only. LibVLC decodes H.264 and H.265
alike and picks the codec up from the SDP, so no client change is needed either way.

### RTSP paths — the trap

The RTSP server accepts *any* path and answers every one of them with a
**byte-identical, canned SDP**. That SDP does not describe the stream you get: it
always advertises H265, carries no resolution, and is unchanged by encoder settings.
Anything the server does not recognise silently falls back to the **sub** stream.

So a wrong path looks completely healthy — it connects, plays, and shows video. It is
just the wrong stream.

Only two paths deliver the main stream, confirmed by decoding rather than by
`DESCRIBE`:

| Path | Stream |
|------|--------|
| `/stream1`, `/h264/ch1/main/av_stream` | main |
| `/stream0`, `/h264/ch1/sub/av_stream` | sub |
| anything else (`/0/av0`, `/main`, `/live/0`, `/cam/realmonitor?...`) | falls back to sub |

Note `/stream0` is the **sub** stream and `/stream1` is **main** — the opposite of
what the names suggest. Both were verified by changing one stream's codec and
observing which path followed.

**If you add a camera model, verify its paths by decoding a frame, not by reading the
SDP.** `DESCRIBE` is actively misleading on this firmware.

### A malformed charset header breaks .NET clients

The CGI replies with `Content-Type: application/json; charset='utf-8'` — the encoding
name is **quoted**. .NET rejects that (`'utf-8' is not a supported encoding name`), so
`HttpContent.ReadAsStringAsync` throws on *every* API call. The client reads response
bodies as bytes and decodes UTF-8 itself to sidestep this.

### Live image tweaks

Alongside the whole-object write, single parameters can be set individually — used
here for responsive slider dragging. Note this is `set_single`, not `set`, and it
needs **both** `param` and `param2`; sending it as a one-argument `set` is rejected
with `param num error`:

```json
{ "mod": "image", "cmd": "set_single",
  "param":  { "channel": 0 },
  "param2": { "cmd": <opcode>, "value": <n> } }
```

| Opcode | Parameter |
|--------|-----------|
| 0 | brightness |
| 1 | contrast |
| 2 | saturation |
| 3 | sharpness |
| 4 | `drc_strenght` *(firmware's spelling)* |
| 5 | `max_led_brightness` |
| 6 | `mwb_red` |
| 7 | `mwb_blue` |

### Encoder configuration

Per-stream, with the stream named in the command and the channel passed as a
JSON-encoded query parameter:

```
GET  mod=video&cmd=get_main&param2={"channel":0}
GET  mod=stream_ability&cmd=get_main&param2={"channel":0}
POST { "mod":"video", "cmd":"set_main",
       "param":  { "channel": 0 },
       "param2": { "enc_type":0, "width":1920, "height":1080,
                   "framerate":20, "gop":30, "rc_mode":0,
                   "bitrate":2048, "quality":4 } }
```

`stream_ability` reports what the hardware will accept — `res_list`/`res_cnt`,
`min`/`max` for framerate, bitrate and GOP, and `venc_set` as a codec bitmask
(`&1` = H.264, `&2` = H.265). The app builds its dropdowns from this, so it only
ever offers combinations the camera supports. `enc_type` is `0` for H.264 and `1`
for H.265.

Every encoder field, **including `enc_type`, applies immediately** — no reboot, no
commit step. Verified by flipping the main stream H.265 → H.264 → H.265 with no
reboot and decoding the stream after each change.

---

## Configuration file

`cameras.json` drives the UI. The Image Settings pane is generated entirely from it,
so supporting a new setting is an edit here rather than a code change.

A profile is selected by matching its `match` block against what `mod=device&cmd=get`
reports; if nothing matches, the first profile is used, so an unrecognised but
API-compatible camera still works.

Settings whose `key` the camera does not report are skipped automatically, and a group
with no surviving settings is hidden — one file can cover models with differing
feature sets.

### Control types

```jsonc
{ "key": "brightness", "label": "Brightness", "type": "slider",
  "min": 0, "max": 255, "fastCmd": 0 }

{ "key": "wdr_enable", "label": "WDR", "type": "toggle" }

{ "key": "rotation", "label": "Rotation", "type": "choice",
  "options": [ { "value": 0, "label": "0°" }, { "value": 2, "label": "180°" } ] }
```

`fastCmd` is optional and enables the live single-parameter write above. Writes are
throttled to one per 150 ms while dragging.

If the camera reports a value not present in a `choice` list, the label turns orange
and a tooltip shows the raw value, rather than silently overwriting it on Apply.

### Discovery

```jsonc
"discovery": {
  "probePort": 80,
  "loginPath": "/view/login.html",
  "signature": "realm = \"CAMERA\"",
  "connectTimeoutMs": 400,
  "maxParallel": 128,
  "subnets": [ "192.168.144", "192.168.1" ],
  "defaultAddresses": [ "192.168.1.10", "192.168.0.123" ]
}
```

Discovery sweeps a `/24` with direct TCP connects and confirms each hit by fetching
the login page and looking for `signature`. ICMP is deliberately not used — these
cameras often ignore ping but always answer TCP, and a stale ARP cache makes a ping
sweep miss devices.

`subnets` is the **entire** search scope. Local interface subnets are never added
implicitly, so the app can only ever reach a network you have named. Add entries and
restart.

Discovery then runs continuously:

| Key | Meaning |
|-----|---------|
| `continuous` | keep sweeping in the background (set false for a single pass at startup) |
| `refreshSeconds` | rest between sweeps, minimum 5 |
| `missesBeforeRemoval` | consecutive misses before a camera is dropped; it shows as offline first |

A camera is identified once, on first sight. Later sweeps only confirm it still
answers, so the steady state costs one TCP connect per host rather than a login each
time round.

### Subnets this machine cannot reach

A subnet can only be searched if the machine has an address on it — listing
`10.20.30` while every adapter sits on `192.168.x` means that sweep can never find
anything, silently.

At startup, and whenever the subnet list changes, unreachable subnets are detected. If
`autoConfigureInterface` is on, the app offers to borrow a temporary address on each,
and removes them again when it closes:

```jsonc
"autoConfigureInterface": true,
"interfaceAlias": "",          // empty: the adapter already carrying the most addresses
"temporaryHostFirst": 200,
"temporaryHostLast": 250
```

- **Nothing changes without consent.** You are asked first, and Windows then prompts
  for administrator rights — one prompt covers the whole batch.
- **Existing addresses are never touched.** Only additions are made.
- **The borrowed host is probed first** — ping and a TCP connect on 80/554/443 — so it
  cannot collide with a camera already on that address.
- **Everything is given back on exit.** Additions are journalled to
  `%LOCALAPPDATA%\CameraSetup\temporary-addresses.json` *before* being applied, so an
  address left behind by a crash is removed on the next start rather than
  accumulating on the adapter.

Set `autoConfigureInterface` to false to have the app only report the problem.

`defaultAddresses` is always probed in addition to the swept range, so a camera
sitting on a factory-default address outside your subnet is still found. Note that
reaching such a camera still requires a local IP on its subnet — add a second IP to
your NIC if it is on an unrelated network.

---

## Parameter files and provisioning

The **Provisioning** tab handles bulk work. It is the one tab that stays usable while
disconnected, because auto-configure finds and logs into cameras by itself.

### Export / import

**Export** captures the connected camera to a parameter file: image settings, both
encoder profiles, its network block, and **every module the app can configure** -
the three detectors, OSD, time, recording, privacy mask, audio in and out, email,
FTP, RTSP, ONVIF, snapshot, timed snapshot, HTTP, serial, cloud, GB28181 and the
alarm centre - plus the source model, firmware and timestamp. A full capture of the
test camera is about 38 KB. **Import** replays one onto the connected camera, after a
confirmation naming the file and where it came from.

The captured set is *derived from `cameras.json`*, not listed in code: adding a module
to the config makes it appear in the UI and in parameter files at the same time,
so the two can never drift apart.

**Per-device identity is deliberately not captured.** Replaying one camera's identity
onto another collides exactly the way a duplicated IP address does, so these are
stripped at capture: `net.mac`, `p2p.p2p_id`, `p2p.qrcode`, and GB28181's
`sip_user_id` / `sip_user_name` / `channel_id`. Live readings are stripped for the
same reason a measurement is not a setting - notably `systime.time_sec`, which would
otherwise set every camera's clock to the moment the preset was taken.

Payloads are stored as the firmware's own JSON objects rather than a translated
model, so fields this app does not surface still round-trip intact. A capture of the
test camera is 33 image fields plus both encoder profiles, about 2 KB.

**A parameter file is applied whole, addressing included.** There is no switch for
this — leaving part of a file out made "import" mean two different things depending on
a checkbox that was invisible from the confirmation dialog.

**"Whole" means every setting the file contains, not every setting the camera has.**
Each section is merged over what the camera currently reports: a field the file
carries overwrites, a field it omits keeps the camera's existing value. This matters
because the firmware replaces an entire object on a write, so sending a partial file
straight through would blank everything it did not mention — and files *are* partial,
since read-only fields are stripped out at capture time.

The consequence is worth stating plainly: applying a file captured from one camera to
a *different* camera moves it onto the first camera's address, and if that camera is
still online both end up on it. The confirmation names the address it is about to
take, so read it. When addressing is applied the camera moves and the connection
drops by design — it reappears in the list at its new address a sweep later.

Only files made by **Export** carry a network block. The shipped `Generic` preset
deliberately has none, so applying it never moves a camera; the confirmation says so.

Presets live in `publish\presets`, i.e. inside the deployed folder. That is
deliberate: copy the folder to a technician's laptop and the presets travel with it.
The trade-off is that a hand-deleted publish folder takes the presets with it, which
is why `run.cmd /rebuild` does not delete anything.

### Presets and provisioning

A preset is just a parameter file in the presets folder:

```jsonc
"presets": {
  "directory": "presets"
}
```

The folder is watched, so adding or editing a file updates the list without a restart
and without a refresh button.

Configure one camera the way you want it, export it into that folder, and it appears
in the preset list.

The flow is **click → look → apply, one camera at a time**:

1. **Click a camera** in the live list. It connects and starts streaming.
2. **Look at the preview** — it is always on screen, and it is the camera you are
   about to change.
3. **Apply to selected camera** on the bottom bar applies the preset to the connected
   camera only, after a confirmation naming the preset and, when the preset carries
   addressing, the address the camera is about to take:

   > Overwrite all settings from preset Generic? New IP address is 192.168.144.60

Nothing is written to a camera you have not connected to. There is no
apply-to-everything action, deliberately — see below.

A preset that carries no network block leaves the camera where it is, and the
confirmation simply omits the address sentence.

### Two safety rules, learned the hard way

**Only explicitly configured subnets are searched.** `discovery.subnets` is the whole
list. Local interface subnets populate the manual Scan dropdown — which is read-only
— but are deliberately *not* searched for provisioning.

This exists because an earlier build did sweep every local interface and offered a
single "apply to every camera found" button. On a developer machine with several
NICs, that reached an unrelated subnet, found a second camera nobody had mentioned,
and rewrote its image and encoder settings. Nothing was lost that mattered, and
addressing was never touched — but only by luck of the design, not by intent.

**Bulk write was removed entirely.** Provisioning acts on one selected camera at a
time. A destructive operation whose blast radius depends on what happens to answer a
network scan is the wrong shape, however many confirmation dialogs guard it.

---

## H.264 support

Fully working, and **no reboot is required**.

Selecting the codec is in the **Stream** tab, gated by the `venc_set` capability mask
so H.264 only appears on hardware that reports it. The test camera reports
`venc_set: 3` — both codecs. Playback needs no client change either way: LibVLC
decodes H.264 and H.265 alike.

Verified on hardware by flipping the main stream H.265 → H.264 → H.265 with no reboot
between changes and decoding a frame each time. Each change took effect within about
four seconds. The sub stream was held on H.265 throughout as a control, and stayed
H.265.

### How this was nearly missed

An earlier round of testing concluded that the codec change required a reboot. That
was wrong, and the way it was wrong is worth recording:

1. The app was probing `/0/av0`, believing it to be the main stream. It is not — it
   is an unrecognised path, so the camera served the **sub** stream.
2. Only the **main** stream had been switched to H.264. The sub stream was still
   H.265, correctly.
3. `DESCRIBE` on the real main path *also* reports H265, because the SDP is canned.

So the evidence said "H.265" for three independent reasons, none of which was the
encoder ignoring the setting. A reboot was performed and changed nothing — correctly,
since there was nothing to fix.

The lesson is in the code: **verify a stream by decoding it, not by asking it.**
`scratchpad/codecprobe` was written for this — it plays each candidate path and
reports the real fourcc and resolution from the decoder. Resolution is the reliable
discriminator, since main and sub differ.

---

## Using it

The camera list is live. The app scans the configured subnets continuously and keeps
the list in step by itself — there is no Scan button to press.

1. **Click a camera in the list.** It connects and starts streaming. No Connect, no
   Play.
2. If it cannot sign in, the row reads *sign in needed* and clicking it opens a
   credential prompt with a **Remember these credentials** option. Saved logins mean
   that camera connects on one click from then on.
3. The **preview is always on screen**, above the settings tabs — you can watch the
   picture while dragging a slider, rather than switching away from it. Drag the
   splitter to trade preview size against panel size.
4. **Everything writes as you change it.** There is no Apply button: edits are sent
   to the camera debounced a few hundred milliseconds after you stop. The Network tab
   is the deliberate exception — address, mask, gateway and DNS move together, so it
   keeps **Save** and **Revert**.

### The tabs

| Tab | What it holds |
|---|---|
| **Image Settings** | Brightness through to day/night behaviour, generated from `cameras.json`. |
| **Detection** | One sub-tab per detector the camera has — Human, Motion, Alarm in, and on capable hardware Vehicle, Perimeter and Sound. Each has its linkages, output modes, a 7-day schedule, and a drawable region where the detector supports one. |
| **OSD** | The date/time overlay and five text lines. |
| **Services** | The smaller settings modules, one sub-tab each: Time/NTP, Recording, Privacy mask, Audio in and out, Email, FTP, RTSP, ONVIF, Snapshot, Timed snapshot, HTTP, Serial, Cloud (P2P), GB28181, Alarm centre. |
| **Users** | The camera's own accounts — add, set password, delete. |
| **Stream** | Codec, resolution, frame rate and bitrate for the main and sub streams. |
| **Network** | Addressing, plus **All Net Connect**, **IP adaptive** and **DHCP**. |
| **Device** | Firmware details, reboot and factory reset. |

**Tabs you do not see are features the camera does not have.** Every detector,
service and linkage is gated on the device's `system_function` capability word, and
the generated tabs are rebuilt whenever you select a camera with a different profile
or capability set — so the UI always describes the camera in front of you rather than
the last one you touched.

### Drawing a region

Detection regions and the privacy mask share one editor: **Edit region…** opens the
current preview frame, and you drag rectangles onto it. Up to four; none means the
whole image. The firmware stores them normalised across the frame rather than in
pixels, so a region survives a resolution change and does not depend on the size of
the window you drew it in.

### Menus

**File** — export the connected camera's parameters to a file, import one back, open
the presets folder. There is no network toggle: a file is applied whole, and whether
the camera moves depends only on whether the file carries a network block.

**Settings → Subnets to search** — edit the watched subnets, whether to keep sweeping,
and how often. Saving writes `cameras.json` and restarts discovery immediately; no
restart needed. Entries are validated, and the file's comments and every other setting
are preserved (it is rewritten via a temporary file, so a failure cannot truncate it).

### The preset bar

The bar along the bottom is global rather than a tab: pick a preset and **Apply to
selected camera**. It acts on the camera selected in the list and nothing else; the
confirmation names the preset and, when the preset carries addressing, the address the
camera is about to take.

A preset now covers the **whole** camera — image, both encoders, addressing, and every
module the app can configure — so applying one to a *different* camera is a wide
change: it carries RTSP/ONVIF/HTTP ports, email and FTP credentials and GB28181 server
settings along with the picture settings. Per-device identity never travels: `mac`,
the P2P id and QR code, and the GB28181 SIP ids are stripped at capture, as are live
readings such as `systime.time_sec`, which would otherwise set every camera's clock to
the moment the preset was taken.

Rows show what each camera is actually running (`H.265 1280x720 @ 20fps`), so you can
see the fleet's state without connecting to anything.

A camera that stops answering is greyed to *offline* first and only dropped after
`missesBeforeRemoval` consecutive misses, so a brief blip does not make rows vanish
under your cursor.

**Saved credentials** are kept in `%LOCALAPPDATA%\CameraSetup\credentials.json`,
with each password encrypted by DPAPI for your Windows account — the file is useless
on another machine or under another account. Right-click a camera for **Reconnect**
and **Forget saved credentials**; forgetting asks for confirmation, because a stray
Enter should never silently delete a stored login.

There is no toolbar. Clicking a camera connects it, credentials come from the config
or the sign-in prompt, and the watched subnets are shown under the list they describe
— not in the status bar, so a sweep every few seconds cannot bury your own action
messages.

Default credentials for a camera with no saved login come from the matched profile's
`auth.defaultUsername` / `auth.defaultPassword` in `cameras.json`.

Changing the IP drops the connection by design. The app follows the camera: the row
it left is removed from the list, the new address is polled until it answers, and the
camera is then selected and reconnected with the preview restarted — no clicking
around. The same happens when a preset or parameter file carries a new address.

A camera is never acknowledged when it accepts an address change; it takes the
address and answers on the new one, but the request it arrived on simply never
returns. That is expected, and is reported as a move rather than a failure.

Switching to DHCP disconnects and asks you to re-scan, since the new address is not
knowable in advance. A **factory reset** removes the camera from the list immediately,
because the address goes with the settings.

Unhandled errors are written to `%LOCALAPPDATA%\CameraSetup\crash.log` with full
stack traces, and the dialog points at the file.

---

## Preview latency

Latency is dominated by buffering, not decoding, so the defaults trade smoothing for
responsiveness:

```jsonc
"rtsp": {
  "transport": "tcp",
  "networkCachingMs": 300,
  "lowLatency": true,
  "extraOptions": []
}
```

> **Never set `networkCachingMs` below 300.** At 100 ms this camera's picture freezes
> on the first frame while libvlc carries on decoding at full rate — it looks exactly
> like a broken preview, and it happens on both TCP and UDP. Measured by hashing
> decoded frames: 100 ms gives 1 distinct frame in 129; 300 ms gives 189 in 189. The
> app clamps the value to 300 regardless of the config, so this cannot be
> reintroduced by editing the file.

`networkCachingMs` is the main latency knob — VLC's own default is 1000 ms, so 300 is
still well under stock. `lowLatency` adds `clock-jitter=0`, `clock-synchro=0`,
`drop-late-frames`, `skip-frames`, `no-audio` and hardware decoding; these were
measured to be safe at 300 ms and above. Anything in `extraOptions` is appended raw.

### Diagnosing a frozen preview

The line under the picture reports what is actually happening:

```
20 fps drawn   vlc 21 fps   locks 330   frame 962F5FC7
```

- **fps drawn** — frames written into the WPF bitmap
- **vlc fps** — frames libvlc reports presenting
- **locks** — times libvlc asked for the frame buffer
- **frame** — a hash of the current picture's pixels

If `frame` stops changing while the counters climb, frames are arriving but the
*content* is identical — that is the caching fault above, not a rendering problem.
A still scene and a frozen stream are indistinguishable by eye, which is precisely
what makes this worth measuring rather than guessing at.

Two things matter more than client tuning:

- **GOP.** A long I-frame interval delays start-up and adds latency; the Stream tab
  exposes it. The test camera shipped with `gop: 1` — every frame a keyframe, which
  is excellent for latency and wasteful of bitrate.
- **Substream.** Lower resolution decodes and traverses the network faster. If you
  only need the preview to judge framing and exposure, the substream is quicker.

The preview never uses LibVLCSharp's `VideoView`. Whichever engine decodes, frames end
up in a `WriteableBitmap`, so the picture is ordinary WPF content: it scales with
`Stretch="Uniform"`, layers normally, and carries no embedded child window. That also
removed a whole class of hosting problems — an unrealised `VideoView` could take the
process down natively, with no managed exception and no crash log.

### Two engines

`preview.engine` selects between them:

- **`ffmpeg`** (default) — pipes raw BGRA frames out of an `ffmpeg` subprocess with
  `-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0`. Needs
  `ffmpeg.exe` beside the app or on PATH; falls back to VLC if absent, unless
  `fallBackToVlc` is false.
- **`vlc`** — libvlc via its raw video callbacks, subject to the 300 ms caching floor
  described above.

### Measured latency, and a correction

Timed by flipping the camera's image and measuring how long until a decoded frame
changed — five samples each, polled per frame, no UI sampling bias:

| engine | median | min | max |
|--------|--------|-----|-----|
| ffmpeg | **58 ms** | 57 | 66 |
| vlc (300 ms caching) | 63 ms | 59 | 70 |

**ffmpeg is about 5 ms faster, not the 200 ms the earlier note in this file
predicted.** That prediction assumed VLC's `network-caching=300` translated into 300 ms
of added delay. It does not: with `drop-late-frames` and `clock-jitter=0` libvlc treats
it as a ceiling, not a fixed queue, so it never holds anything like that much.

At ~58 ms both engines are close to the camera's own floor — encode plus network — so
the client is no longer what is worth optimising. The remaining reasons to prefer
ffmpeg are that it has no minimum-buffer constraint to trip over and no native
in-process decoder to crash the app, rather than speed.

---

## Adding another camera model

1. Log into its web GUI, then read the pages it serves — `/view/` and `/js/` have
   directory listings enabled on this firmware family, which is how the API above was
   recovered.
2. Confirm the auth flow and the `mod`/`cmd` names it uses.
3. Add a profile to `cameras.json` with a `match` block that identifies it.

If a model differs structurally — a different auth scheme or a non-JSON API — that
needs a new transport implementation alongside `CameraClient`. Everything above the
transport (UI generation, discovery, preview) is model-agnostic.

