# Vatilon H8D web API

Reverse-engineered from the camera's own web UI. The full detail - every parameter,
its meaning and the `file:line` it was read from - is in [h8d-api-map.json](h8d-api-map.json);
this page is the index.

Device: **H8D**, `cpu_type ssc378de`, firmware `V1.13.11-20240515`.

## How a request is spelled

```
GET /cgi-bin/web.cgi?cmd=<module>&action=<verb>&username=<user>&password=<token>
                     [&param=<json>][&param2=<json>]

token = HMAC-SHA1(key = username, message = md5hex(password))   lowercase hex
```

The H82 lineage spells the same request `mod=<module>&cmd=<verb>` and presents a
`Session-Id` header from a login step instead. The module and field vocabulary either
side is shared, which is why one profile inherits the other in `cameras.json`.

> **Write slots are reversed.** A channel-scoped write carries the body in `param` and
> the channel in `param2` - the opposite of the H82. The swapped order is not rejected:
> it returns `{"status":"ok"}` and writes a zeroed record.

## Endpoints

### auth-session  (7)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `account` | `add` | write | param | certain |
| `account` | `check` | read | - | certain |
| `account` | `delete` | write | param | certain |
| `account` | `list` | read | - | certain |
| `account` | `modify` | write | param | certain |
| `device` | `get` | read | - | certain |
| `language` | `get` | read | - | certain |

CORRECTIONS TO THEIR MAP

1. FALSE "certain" claim. They wrote that language/get "is the only web.cgi call in the whole mirror that sends NO username/password". It is not. js/hardware_config.js:1 issues `{action:"get",cmd:"hardware_config"}` and `{action:"set",cmd:"hardware_config",param:a}` with no credentials either. Their reachability-probe advice still stands, but the uniqueness claim is wrong, and "this endpoint is unauthenticated" is an inference from what the JS sends, not proof the firmware skips the check - downgrade to likely.

2. Malformed parameter list on account/delete: they emitted two `cmd` rows, one with values "delete". Artefact; there is one `cmd`, value "account".

3. The `_` parameter is not part of the API and is mis-described. It is jQuery's cache-buster, appended to EVERY GET in the whole UI (every page calls $.ajaxSetup({cache:false})), not just login. jquery-1.10.2.min.js: `vn=x.now()` then `"_="+vn++` - a counter seeded from Date.now() at page load and incremented per request, not a fresh epoch-ms per call. Omit it entirely from a client.

4. MISSED SURFACE - a second way to establish a session. view/config.html:113-138 is an auto-login entry point: GET /view/config.html?username=<u>&password=<base64(utf8(PLAINTEXT))>. It base64-decodes (js/base64.js), md5s, HMACs, then calls account/check itself and, on success, seeds the cookies with a 24-hour `expires` (view/config.html:97) instead of login.html's 30 minutes (view/login.html:44). Two consequences worth flagging: the plaintext password travels in a URL query string, and any caller can mint a 24h UI session by fetching this page.

5. THEIR COOKIE ANALYSIS IS CORRECT AND I CAN STRENGTHEN IT. Every credential cookie is written with a bare `document.cookie="name=value"` and no path, so per RFC 6265 the default-path is /view and none of temporary/username/password/expires/rtsp_username/rtsp_password is ever sent to /cgi-bin/web.cgi. The only path=/ cookie in the mirror is the UI language cookie, whose name is `icbs_language` (js/language.js, LM.SetCookie). Stronger evidence than the RFC argument: `temporary` is read back in exactly two places (view/login.html:29, view/config.html:131) and in both it is used only to stringify the CryptoJS WordArray - nothing anywhere reads it for authentication. So the established fact's "the token is set as cookie temporary=<token>" is true as a statement about the browser but misleading as a statement about the protocol: the query parameter is what authenticates, and `.toString(CryptoJS.enc.Hex)` reproduces the round-trip.

6. TOKEN DERIVATION - confirmed exactly, with the primitives checked. js/hmac_sha1.js: `_createHmacHelper:function(a){return function(b,d){return (new u.HMAC.init(a,d)).finalize(b)}}` so the call signature is HmacSHA1(message, key) - message=hex_md5(pwd), key=username. Default toString encoder is Hex with `.toString(16)` per nibble => lowercase, 40 chars. js/md5.js sets hexcase=0 (lowercase) and chrsz=8, and str2binl does `charCodeAt(i)&0xFF` - i.e. the password is hashed as the LOW BYTE of each UTF-16 code unit, NOT as UTF-8. A C# client must replicate that byte reduction, not use Encoding.UTF8, or non-ASCII passwords will diverge. The HMAC key (username) is parsed as real UTF-8 by CryptoJS, so the two inputs use different encodings.

7. THERE IS NO LOGOUT ENDPOINT - confirmed. view/head.html:51 calls change_to_login() (view/head.html:23-27) which only refreshes `expires` to now+30min and navigates to login.html. It touches no CGI and does not clear the credential cookies; the token survives "Exit". Grepping every cmd value in the mirror yields no session/login/logout/token module - the account module has exactly five verbs: check, list, add, modify, delete.

8. "SESSION" IS PURELY CLIENT-SIDE. There is no server session at all. Every authenticated call carries username+password params; the only session artefact is the `expires` cookie (epoch ms). Every settings page runs the same onload guard - if the cookie is missing or in the past, redirect to login.html, else rewrite it to now+1800000 (e.g. js/system_setting.js:1, js/rtsp_setting.js:1, js/user_setting.js:1). A native client can ignore `expires` entirely and just re-send credentials on every request. No Session-Id header, no Authorization header, no POST anywhere in the mirror - grep for setRequestHeader/headers:/type:"POST" returns nothing outside jquery itself.

9. PARAM ORDER IS NOT SIGNIFICANT. account/check is emitted as cmd,action,username,password from login.html and as action,cmd,username,password from config.html; user_setting.js uses action,cmd,username,password,param. Same endpoint, both orders shipped, so the server is order-insensitive.

10. THE "ONE ENDPOINT" PREMISE IS NOT QUITE TRUE, and the exceptions are auth-relevant. GET /version - plain-text web version string, no credentials (js/utils.js:1, fetched on every page load). POST /cgi-bin/upload_file.cgi - multipart config import, no credentials in the form or the follow-up GET (view/system_guard.html:126 and js/system_maintenance.js:1 import_config_file). GET /config.conf from the web root after cmd=system&action=export_config - the whole device config, served unauthenticated from the root (js/system_maintenance.js:1 downloadFile). /version is the cheapest liveness probe for the Windows app and needs no credentials at all.

11. CREDENTIAL FAN-OUT the app will care about. Login also stores rtsp_username=<user> and rtsp_password=<PLAINTEXT pwd> (view/login.html:49-50; view/config.html:103-104; rewritten on account/modify). js/player.js:1 reads those two cookies and feeds them to _WsClient_OpenStream1 against ws://<host>:9101 - so the stream credentials are the plain account credentials, not the token. Whether they are actually required is governed by `auth_enable` in the rtsp module (cmd=rtsp&action=get|set|default, js/rtsp_setting.js:1, param {auth_enable:0|1, enable:1, port:int}) and independently by cmd=onvif (js/onvif_setting.js:1, param {auth_enable, enable, port}). Those endpoints belong to the rtsp/onvif subsystems but they decide whether an RTSP URL needs user:pass.

12. Minor, but worth knowing: the login form allows a 16-char password while the user_setting add/modify forms cap at 15, so a password set in the web UI can never exceed 15 chars. checkS also forbids symbols outright and requires a letter+digit mix.

13. Client timeouts observed, if you want to match UI behaviour: 2000 ms on login.html:19 and user_setting.js, 4000 ms on rtsp/hardware pages. js/utils.js:1 also sets a global $.ajaxSetup({async:false}) that login.html:18 overrides but config.html does not.

I made no network requests and did not touch 192.168.144.54; everything above is read out of the mirrored files.

### detection-alarm  (26)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `alarm_center` | `default` | read | - | certain |
| `alarm_center` | `get` | read | - | certain |
| `alarm_center` | `set` | write | param | certain |
| `alarm_center` | `test` | action | - | certain |
| `alarm_in` | `default` | read | - | certain |
| `alarm_in` | `get` | read | - | certain |
| `alarm_in` | `set` | write | param | certain |
| `alarm_out` | `default` | read | - | certain |
| `alarm_out` | `get` | read | - | certain |
| `alarm_out` | `set` | write | param | certain |
| `alarm_out` | `test_email` | action | - | certain |
| `fd` | `default` | read | - | certain |
| `fd` | `get` | read | - | certain |
| `fd` | `set` | write | param | likely |
| `fd_invasion` | `default` | read | - | certain |
| `fd_invasion` | `get` | read | - | certain |
| `fd_invasion` | `set` | write | param | certain |
| `md` | `default` | read | - | certain |
| `md` | `get` | read | - | certain |
| `md` | `set` | write | param | certain |
| `pd` | `default` | read | - | certain |
| `pd` | `get` | read | - | certain |
| `pd` | `set` | write | param | certain |
| `voice_detect` | `default` | read | - | certain |
| `voice_detect` | `get` | read | - | certain |
| `voice_detect` | `set` | write | param | certain |

RE-DERIVED INDEPENDENTLY FROM THE MIRROR. Files read: js/motion_detect.js, js/person_detect.js, js/face_invasion_detect.js, js/voice_detect.js, js/alarm_in_setting.js, js/alarm_center_setting.js, js/email_setting.js, js/utils.js, js/language.js, and the matching view/*.html. All are minified onto a single line, so every "file:line" below is ":1" and I have given a byte offset plus a verbatim needle instead; each needle was verified to occur EXACTLY ONCE in its file.

=== WHAT I CHANGED IN THE OTHER ANALYST'S MAP ===

MISSED CALLS (11 of the 26 in this subsystem were absent from what I was shown):
1. cmd=voice_detect get/set/default - the entire audio-detection module, which the subsystem title explicitly claims to cover. js/voice_detect.js, byte 14720 / 10296 / 10700.
2. cmd=alarm_in get/set/default - the physical alarm-input module. js/alarm_in_setting.js, byte 14130 / 9750 / 10141.
3. cmd=alarm_center get/set/default/TEST - four actions, not three. The `test` verb (byte 2177) is easy to miss because it lives between the default and get handlers.
4. cmd=alarm_out get/set/default/test_email - the linkage-output plumbing the subsystem title promises. See the naming trap below.
(Their map was truncated mid-`fd_invasion set` in what I received, so some of these may exist in the part I could not see; I have returned the full corrected map as instructed rather than a diff.)

FACTUALLY WRONG IN WHAT I COULD SEE:
5. cmd=fd_invasion GET responseShape claimed `voice_index:int` is consumed. It is not. `$("#voice_index").val(a.voice_index)` appears ONLY in the fd_invasion *default* handler (js/face_invasion_detect.js:1 byte ~12500), not in the get handler (byte 16059, whose field reads end at the output bitmask). Same asymmetry they correctly spotted for md now applies to fd_invasion.
6. "password ... falls back to literal 123456 if cookie absent" / "username ... if cookie absent" - imprecise. The guard is `null!=C&&null!=D&&(w=C[2],x=D[2])`: BOTH cookies must be present or BOTH defaults ("admin"/"123456") are used. A client that sets only one cookie gets the defaults for both.
7. `duration` "label tag Tag1814, almost certainly seconds" - unsupported. /js/language/ exists on the device (listed in cam54/list_js.html) but was NOT mirrored, so no Tag####->text mapping is available for ANY label in this subsystem. The only hard facts are: integer, client-validated >0, maxlength 6. I have removed the unit claim rather than repeat the guess.
8. `sensitivity:int(1-100)` presented as the response domain. 1..100 is the client-side *write* validation (`0>=parseInt(...)||100<parseInt(...)`); nothing constrains what a read returns. Likewise `rect_num:int(0-4)` and `rect: exactly 4` are read-side inferences from the fixed draw_box0..3 elements, not stated domains - on a read the JS would throw if rect_num exceeded 4.
9. Several endpoints marked "certain" whose response shape the source does not establish. cmd=fd set is the clear case: the success callback is literally `function(c,a){}`, so nothing is known about the success body; I downgraded that endpoint to "likely". cmd=fd get/default are certain as calls but only ONE field (show_face) is evidenced - the module may return more and I say so rather than implying the field list is complete.

CONFIRMED CORRECT in their map (I re-derived each): cmd/action for md, pd, fd, fd_invasion x get/set/default; md set hardcoding show_human=0 and voice_index=0; pd set driving show_human from a checkbox; fd set carrying only show_face; fd_invasion having no sensitivity and no bit-7/FTP term; the md default voice_index read being dead code (there is genuinely no #voice_index element in view/motion_detect.html - grep confirms); the fd-then-fd_invasion nesting; status=="error" as the read failure sentinel and "ok" as the write success sentinel.

=== THE LINKAGE `output` BITMASK (shared vocabulary across all six detection/alarm modules) ===
bit 0 = send_to_client   (push to connected client/app)
bit 1 = UNKNOWN. No UI control anywhere. Every page reads it into a local (`A`, or `t` in voice_detect) and echoes it back unchanged on the next set. Initialised to 0, so a client that writes without a prior read silently clears it. Do not invent a meaning for it.
bit 2 = send_to_email    (consumes the cmd=alarm_out module's SMTP config)
bit 3 = alarm_out_enable (relay / dry-contact output. NOTE: NOT cmd=alarm_out - see trap below. No module in the mirror configures this output.)
bit 4 = voice_output     (audible/siren linkage; absent from voice_detect.html and cleared by voice_detect set)
bit 5 = whitelight_output (white-light illuminator linkage)
bit 6 = UNKNOWN. Same echo-back treatment as bit 1 (`v` in md/pd, `z` in fd_invasion). Not present at all in voice_detect or alarm_in, which therefore clear it.
bit 7 = send_to_ftp      (absent from face_invasion_detect.html and alarm_in.html, and cleared by those pages' sets)
Per-module coverage, verbatim from source:
  md:            a.output=m<<7|v<<6|b<<5|c<<4|l<<3|e<<2|A<<1|h   (all 8 bits)
  pd:            a.output=m<<7|v<<6|b<<5|c<<4|l<<3|g<<2|A<<1|f   (all 8 bits)
  fd_invasion:   c.output=z<<6|a<<5|d<<4|b<<3|q<<2|A<<1|l        (no bit 7)
  voice_detect:  a.output=l<<7|b<<5|c<<3|h<<2|t<<1|k             (no bit 4, no bit 6)
  alarm_in:      a.output=k<<5|l<<4|h<<3|b<<2|c                  (no bits 1, 6, 7)
CLIENT CONSEQUENCE: every `set` in this subsystem is a whole-object replace, and each page's bitmask expression drops the bits it has no checkbox for. A client must READ, MODIFY, WRITE - and must preserve bits 1 and 6 itself, because they exist on the wire but have no discoverable meaning.

=== NAMING TRAP ===
cmd=alarm_out is the SMTP/email module (fields email_server, email_server_port, email_crypto, email_username, email_passwd, email_sender, email_recipient0..4) and is served by js/email_setting.js + view/email_setting.html. The `alarm_out_enable` checkbox on every detection page is bit 3 of `output` and refers to a physical relay output that NO mirrored module configures. Wiring bit 3 to cmd=alarm_out would be wrong.

=== TRANSPORT / WIRE FORMAT ===
- Every call in this subsystem is jQuery `$.get(url, dataObject, cb)`: HTTP GET, params in the query string, no request body, no custom headers (grep for setRequestHeader finds hits only inside jquery-1.10.2.min.js). Zero $.post / type:"POST" in any of the seven files. Auth rides entirely on the `username`/`password` query params plus whatever cookies the browser attaches.
- `param` is JSON.stringify'd then serialised by jQuery's $.param(), i.e. application/x-www-form-urlencoded rules: space becomes `+`, and {}"":, are percent-encoded. A hand-rolled client must URL-encode the JSON the same way.
- Every page does `$.ajaxSetup({cache:!1})`, so jQuery appends a cache-buster `_=<epoch_ms>` to EVERY request. The firmware evidently tolerates the extra param; it is not required.
- Ajax timeout is 2000 ms on the five detection/alarm-input pages and 4000 ms on alarm_center_setting.js and email_setting.js (the two pages with `test` verbs).
- Responses are consumed as objects (`a.status`, `a.enable`, `a.schedule[0].begin1.hour`) with no explicit dataType, so the device must be returning Content-Type: application/json. All config fields are TOP LEVEL - there is no `data` wrapper anywhere in this subsystem, unlike the login call's {"status":"ok","data":""}.
- Read handlers test `if("error"!=a.status)`. A response that omits `status` entirely therefore takes the SUCCESS path (undefined != "error"). Write handlers test `"ok"==a.status`, which is stricter.

=== SCHEDULE STRUCTURE (identical in all six detection/alarm modules) ===
`schedule` is an array of exactly 8 entries; index 0..7 map to checkboxes alarm_time0..alarm_time7 and to input ids time<N>_start1/_end1/_start2/_end2/_start3/_end3. Each entry: {enable:0|1, begin1,end1,begin2,end2,begin3,end3}, each of those six being {hour:int, minute:int, second:int}. So: 8 slots x 3 time windows. The mirror does NOT say whether index 0..7 means Sunday-first, Monday-first, or something else - the labels are i18n tags and the dictionary is missing. Do not assume days of the week.
Schedule validation differs by page and is worth knowing before you send something the device may reject: js/motion_detect.js, js/person_detect.js and js/voice_detect.js all stub it out (`window.check_schedule=function(a,d){return 1}`), while js/alarm_in_setting.js and js/face_invasion_detect.js enforce it (`return(0!=a||0!=d)&&a>=d?0:1` - i.e. reject begin>=end unless both are 00:00:00). Whether the firmware itself validates is not observable.

=== REGION (rect) GEOMETRY ===
`rect` is always exactly 4 objects on a write; `rect_num` says how many leading entries are meaningful and the rest are sent as {x:0,y:0,w:0,h:0}. Units are a normalised 0..10000 grid: `h.x=Math.round(g/c.offsetWidth*1E4)` on write, `a.rect[b].x/1E4*h.offsetWidth` on read. Present on md, pd and fd_invasion; ABSENT from voice_detect and alarm_in.

=== SESSION / PAGE-LOAD BEHAVIOUR A CLIENT SHOULD KNOW ===
- Cookies the JS depends on: `username` and `password` (the latter holding the HmacSHA1 token, set by login.html:48 to the same value as cookie `temporary` from login.html:27). Confirmed: `var encry_password=hex_md5(pwd); var sha1_result=CryptoJS.HmacSHA1(encry_password, username);` at view/login.html:24-25 - the established auth facts hold.
- `expires` is a CLIENT-ONLY session guard: every page's window.onload checks it and bounces to login.html, then re-stamps it to now+1800000 ms. It is not sent to or validated by web.cgi. A non-browser client can ignore it entirely.
- Page load fires: motion_detect -> get_md_param(); person_detect -> get_pd_param(); face_invasion_detect -> get_pd_param() (misnamed; it actually issues cmd=fd get then cmd=fd_invasion get); voice_detect -> get_voice_detect_param(); alarm_in -> get_alarmin_param(); alarm_center_setting -> get_alarm_center(); email_setting -> get_email().
- The face page's Save button runs do_save_fd_attr() then do_save_fd_invasion() back to back - two independent async GETs (cmd=fd set, then cmd=fd_invasion set) with no ordering guarantee and no combined error handling.

=== CAPABILITY GATING (cross-subsystem, but you need it to know which of these modules exist on a given unit) ===
view/left_menu.html:151-169 calls `/cgi-bin/web.cgi?action=get&cmd=device` and tests `data.system_function` bitwise:
  bit 0  -> md supported          bit 1  -> pd supported        bit 2  -> fd supported
  bit 7  -> alarm_in supported    bit 9  -> email supported     bit 12 -> voice_detect supported
(bits 6, 10, 13, 14, 15 gate gb28181, region_cover, image, snapshot_res, cloud - other subsystems.)
There is no capability bit for alarm_center; its menu entry is unconditional (left_menu.html:309). Worth flagging: this one call uses HARDCODED `username="admin"` / `pwd="123456"` (left_menu.html:146-147) and never reads the cookies, unlike every other page in the mirror.

=== THINGS THAT ARE NOT THERE (so nobody re-hunts for them) ===
- No `alarm_setting.html` page and no cmd for a standalone alarm-output/relay module. The menu entry is commented out at view/left_menu.html:297 and the file is not in view/ (36 files listed in cam54/list_view.html, none matching).
- No sensitivity control on the face page or the alarm-input page.
- No POST anywhere, no Session-Id header, no digest/realm/nonce - none of the Vatilon H82 dialect in CameraClient.cs applies here.
- /js/language/ (the Tag#### dictionary) and /js/layui/ are directories on the device per cam54/list_js.html but were not mirrored, which is the single biggest gap: it is why the meaning of `alarm_in.type` 0 vs 1, the duration unit, and the schedule slot ordering all have to stay unresolved rather than guessed.

### device-system  (18)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `(not web.cgi) GET /config.conf` | `download the exported config, issued ~2000 ms after cmd=system&action=export_config` | read | - | certain |
| `(not web.cgi) GET /version` | `read the web-UI asset version string used for cache-busting` | read | - | certain |
| `(not web.cgi) POST /cgi-bin/upload_file.cgi` | `import/restore a configuration file` | write | - | unsure |
| `auto_reboot` | `default` | read | - | certain |
| `auto_reboot` | `get` | read | - | certain |
| `auto_reboot` | `set` | write | param | certain |
| `device` | `default` | read | - | certain |
| `device` | `get` | read | - | certain |
| `device` | `set` | write | param | certain |
| `hardware_config` | `get` | read | - | certain |
| `hardware_config` | `set` | write | param | certain |
| `language` | `get` | read | - | certain |
| `system` | `export_config` | action | - | certain |
| `system` | `reboot` | action | - | certain |
| `system` | `reset` | action | - | certain |
| `systime` | `default` | read | - | certain |
| `systime` | `get` | read | - | certain |
| `systime` | `set` | write | param | certain |

CORRECTIONS TO THE MAP I WAS GIVEN (their map was truncated mid-entry at "cmd": "aut, so some of these may be additions rather than fixes):

1. MISSED ENDPOINT - cmd=language&action=get. Called with NO username and NO password at all, from the login page before any authentication (view/login.html:127-129), from view/config.html:31-33, and from js/hardware_config.js:1. This is the cleanest unauthenticated probe on the box. Their map only referenced it in prose ("see cmd=language") without listing it.

2. MISSED ENDPOINTS - cmd=hardware_config&action=get and action=set. Both are sent with NO auth parameters whatsoever: `$.get("/cgi-bin/web.cgi",{action:"get",cmd:"hardware_config"},...)` and `$.get("/cgi-bin/web.cgi",{action:"set",cmd:"hardware_config",param:a},...)`. Their map's subsystem title claims "hardware capability config" but the truncated portion may or may not have had them.

3. FALSIFIES "every call carries username+password". Three separate call sites omit both params entirely (the two hardware_config calls and cmd=language), and js/hardware_config.js:1 sends `username:"admin",password:"123456"` HARDCODED for cmd=device&action=get - literally the plaintext default password, not a token. Since the same page's other calls send nothing at all and the page works, authentication is almost certainly by cookie (login.html:27/48 sets both `temporary=<token>` and `password=<token>`), and the query params are decorative for at least some modules. This directly bears on the established facts: the token is set as BOTH cookies, and the query `password` param may be redundant. A client should set the cookies AND pass the params, which is what the majority of the JS does.

4. SOURCING ERROR in their device get entry. They cite js/system_setting.js:1 as the source for system_function, cpu_type, home_ipc, chn_num and stream_num_per_chn. system_setting.js reads NONE of those - it reads only devtype, nickname, serial_num, uboot_version, kernel_version, version, language, voice. The capability fields come from view/left_menu.html:159-169 (system_function bits), js/hardware_config.js:1 (home_ipc, cpu_type), js/player.js:1 (chn_num, stream_num_per_chn), js/region_cover.js:1 / js/osd_setting.js:1 / js/video_setting.js:1 / js/snapshot_res_setting.js:1 / js/image_setting3.js:1 (chn_num). The claim is right; the citation is not.

5. OVERSTATED CERTAINTY on device set. "serial_num / uboot_version / kernel_version / version / system_function are read-only" is not supported - the source only shows the UI does not send them. Downgraded to a statement about what is sent.

6. UNDERSTATED CONFIDENCE on device default: they marked it "likely". It is verbatim in js/system_setting.js:1 and wired to a button at view/system_set.html:63. Raised to certain.

7. OVERSTATED CONFIDENCE on the config import. They marked POST /cgi-bin/upload_file.cgi "likely". The hosting fieldset #import_config_ui is display:none at view/system_guard.html:120 and nothing in the entire mirror ever un-hides it (unlike #export_config_ui and #hardware_setting_ui, which are explicitly revealed by system_maintenance.js). The form and handler are dead code in this build. Downgraded to unsure.

8. NO INVENTED PAIRS FOUND. I enumerated every action/cmd pair in the mirror and every cmd/action they listed does exist verbatim. There is no cmd=system&action=get, no cmd=system&action=import_config, and no cmd=hardware_config&action=default - none of which they claimed.

COMPLETE cmd/action inventory for this subsystem (nothing else exists in the mirror): device{get,set,default}, language{get}, systime{get,set,default}, auto_reboot{get,set,default}, system{reboot,reset,export_config}, hardware_config{get,set}. Plus three non-web.cgi endpoints: GET /config.conf, POST /cgi-bin/upload_file.cgi, GET /version.

STRUCTURAL NOTES FOR THE CLIENT:

Transport. Every call is a jQuery $.get, i.e. HTTP GET with everything in the query string, including writes. `param` is a JSON document URL-encoded into a single query parameter. There is no POST anywhere except the (dead) file upload form. No Session-Id header, no digest - the contrast with the Vatilon H82 dialect is total.

Response envelope. Flat JSON with the payload at the TOP level; there is no "data" wrapper (which is consistent with the verified login returning {"status":"ok","data":""} - "data" there is an empty scalar, not a container). Reads test `status != "error"`; writes test `status == "ok"`. Nothing in this subsystem reads an error message field, so the shape of an error body beyond `status` is unknown.

Timeouts. $.ajaxSetup({timeout:2000}) in system_setting.js, system_maintenance.js and time_setting.js; hardware_config.js raises it to 4000. Note js/utils.js:1 does a global `$.ajaxSetup({async:!1})` at load time which the per-page setups then flip back to async - a client can ignore this, but it explains why some pages block.

Session. Cookies set on successful login: username, password (= the token), temporary (= the token), rtsp_username, rtsp_password (= the PLAINTEXT password, so the RTSP credential is stored in cleartext in the browser), expires. `expires` is a client-side-only millisecond timestamp that each page re-stamps to now+1800000 on load (now+86400000 in config.html); pages redirect to login.html when it has lapsed. It is not an auth token and the server does not appear to see it.

Read-modify-write hazard. cmd=hardware_config&action=set echoes back the entire system_function bitmask with only five bits recomputed. Never send a synthesised value - GET first.

Clock setting. There is no dedicated "set time" verb. Setting the clock is cmd=systime&action=set with a non-zero `time_sec` (Unix epoch seconds) and ntp.enable:0; the normal Save path sends time_sec:0, which apparently means "don't touch the clock". Sending a full systime set requires every DST field, so read cmd=systime&action=get first and mutate.

Reboot/reset are fire-and-forget. Both success callbacks are empty; expect the request to time out rather than return.

Config export is a two-step: trigger cmd=system&action=export_config, wait (the UI waits a fixed 2000 ms with no polling and no status endpoint), then GET /config.conf from the web root over plain http.

Language pack absent. /js/language/*.js was not mirrored, so every Tag#### label is unresolvable. That leaves these genuinely undetermined: the `voice` enum (0|1|2|15), auto_reboot `wday` = 7, dst `offset_time` units, ntp `interval` units, and all of hardware_config's photoresistor_type / af_protocol / ptz_track_mode / custom_io_func1 / custom_io_func2 enums. I have not guessed at any of them.

Two firmware-UI bugs worth knowing (they do not change the API): js/time_setting.js reads `a.dst.end_time.second` when composing the DST START time, so the start second is always taken from the end time; and js/hardware_config.js matches the auth cookies but never assigns them, which is why that page falls back to the hardcoded admin/123456.

### image  (16)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `image` | `debug` | action | - | certain |
| `image` | `default` | unknown | - | likely |
| `image` | `default` | unknown | param2 | likely |
| `image` | `get` | read | - | certain |
| `image` | `get` | read | param2 | certain |
| `image` | `set` | write | param | certain |
| `image` | `set` | write | param | likely |
| `image` | `set` | write | param2, param | certain |
| `image` | `set_single` | write | param | certain |
| `image` | `set_single` | write | param2, param | certain |
| `lamp_panel` | `default` | unknown | - | likely |
| `lamp_panel` | `default` | unknown | param2 | likely |
| `lamp_panel` | `get` | read | - | certain |
| `lamp_panel` | `get` | read | param2 | certain |
| `lamp_panel` | `set` | write | param | certain |
| `lamp_panel` | `set` | write | param2, param | certain |

SCOPE / FILE INVENTORY. Only four files in the mirror ever emit cmd:"image": js/image_setting.js, js/image_setting2.js, js/image_setting3.js, js/video_setting.js (verified: `grep -l 'cmd:"image"' js/*.js`). cmd:"lamp_panel" appears in exactly two files, js/image_setting2.js and js/image_setting3.js, i.e. only on image pages, so I have included it here; nothing else in the mirror touches it.

BIGGEST CORRECTION vs. THEIR MAP - image_setting2.js was missed entirely. It is a third image page (view/image_setting2.html loads js/image_setting2.js at html line 13). It issues image get/set/set_single/default in the 4-param (no param2) dialect plus the whole lamp_panel trio. Its `set` payload uses the key "backlight_enable", NOT "backlight_mode" - see js/image_setting2.js:1 byte 2411 `a.backlight_enable=g_image_param.backlight_enable;`. Because that value is echoed straight out of the last `get` response, if the firmware's get actually returns "backlight_mode" (as image_setting.js:1 byte 6068 and image_setting3.js:1 byte 12300 both assume) then g_image_param.backlight_enable is undefined and JSON.stringify silently DROPS the key, so this page would send a 23-key block. I cannot tell from the files which spelling the firmware really uses. Genuinely ambiguous - do not rely on either name without a live probe.

SECOND CORRECTION - a whole cmd/action pair was missed: cmd=image&action=debug. js/image_setting3.js:1 byte 16592, inside window.check_click: after 11 clicks within 5 s on the span at view/image_setting3.html:31 (`onclick="check_click()"`), it fires the call and on non-error toasts the hardcoded Chinese string "开启图像调试模式成功!!!" = "Image debug mode enabled successfully!!!". It is a 4-param call with NO param2, which directly contradicts their claim that param2 is "Present on ALL image and lamp_panel calls made by image_setting3.js" - that claim is false.

THIRD CORRECTION - which page a given camera actually serves. view/left_menu.html:195-205 gates this on cpu_type, not on anything the analyst mentioned: if (image_support) { if (cpu_type=="hi3516ev200" || "hi3516ev300" || "hi3518ev300") show li_image (-> image_setting.html, the 4-param no-channel dialect) else show li_image3 (-> image_setting3.html, the 5-param param2 dialect) }. li_image2 (image_setting2.html) is NEVER un-hidden by any code path in the mirror - it is dead/unreachable from the menu, reachable only by typing the URL. image_support = data.system_function & (0x1<<13) i.e. bit 8192 (left_menu.html:167). PRACTICAL CONSEQUENCE for the Windows app: it must first call cmd=device&action=get and branch on cpu_type to decide whether to send param2 at all. Sending the wrong arity is exactly the "param num error" case described in the brief.

FOURTH CORRECTION - param2 channel domain. They said "Domain 0..chn_num-1". That is right only for video_setting.js, where sensor_change does `channel=parseInt(a)` off a dropdown built with chn_num entries. On image_setting3.js g_channel is only ever written by _change_channel_stream, and change_channel collapses everything to two values: `0==b?_change_channel_stream(0,1):_change_channel_stream(1,1)` (image_setting3.js:1, end of file). So from the image page the observed domain is {0,1} regardless of chn_num. Downgraded to "likely".

FIFTH CORRECTION - key counts. Their image_setting3 `set` is "26 keys"; the source assigns 28: mirror, flip, nr_enable, wdr_enable, backlight_mode, anti_flicker_enable, anti_flicker_mode, ldc_enable, face_mode, smart_face_mode, brightness, saturation, sharpness, contrast, drc_strenght, max_led_brightness, led_brightness_mode, day_begin, day_end, day_night_mode, ircut_delay, tv_standard, exposure, day_night_lux, night_fps_select, rotation, day_to_night_brightness, night_to_day_brightness. Their image_setting.js count of 24 is correct.

SIXTH CORRECTION - "the default handler is a byte-for-byte copy of the get handler" is not quite true. get_image_param assigns the response to a global (image_setting.js:1 byte 9600 `g_image_attr=a;`, image_setting2/3 `g_image_param=a;`); do_default in image_setting.js does NOT (it goes straight to the *_init flags). In image_setting2.js and image_setting3.js do_default DOES assign g_image_param. Behaviourally this matters: on image_setting.js a Save immediately after Restore-Defaults re-sends values scraped from the repainted form, not from a cached object.

SEVENTH CORRECTION - image_setting3.html:139 contains a `<select id="backlight_mode">` with options 0/1/2, but js/image_setting3.js never references "#backlight_mode" (verified: 8 occurrences of the bare string backlight_mode, 0 occurrences of "#backlight_mode"). It is dead UI. The JSON key backlight_mode is derived instead from the single #wdr_enable dropdown: `1==c?(a.wdr_enable=1,a.backlight_mode=0):2==c?(a.wdr_enable=0,a.backlight_mode=1):3==c?(a.wdr_enable=0,a.backlight_mode=2):(a.wdr_enable=0,a.backlight_mode=0)` (image_setting3.js:1 byte ~7100). So on that page wdr_enable is 0|1 only and backlight_mode is 0|1|2, and the two are mutually exclusive - a client must not set both.

EXPOSURE DOMAIN - I nearly repeated a trap here. A naive regex over view/image_setting.html:124 yields option values {0,200000,100000,50000,21..1}, but 200000/100000/50000 are inside an HTML comment `<!-- ... -->` and are not live. The real domain on image_setting.html is {0} plus 1..21 (0 = the Tag2410 label, 21 = "1/20000" down to 1 = "1/5"). Their "0-21" is therefore correct, though for a different reason than stated. On image_setting3 the exposure select is EMPTY in HTML and built at runtime by create_exposure_object(tv_standard) with exactly 14 options, values 0..13 - and the labels differ by tv_standard: tv_standard==0 gives 1/25,1/50,1/100... (PAL/50 Hz), else 1/30,1/60,1/120... (NTSC/60 Hz). That independently confirms tv_standard 0=PAL/50Hz, 1=NTSC/60Hz. Note the same integer means a DIFFERENT shutter speed on the two pages, and even on image_setting3 it means a different speed depending on tv_standard.

HTTP MECHANICS. Every call is jQuery `$.get("/cgi-bin/web.cgi", {...})` - there is no $.post, no type:"POST", no $.ajax( anywhere in any of the four files. All parameters are query-string. jQuery serialises the object in literal declaration order, so the wire order is action, cmd, username, password[, param2][, param] - i.e. action comes BEFORE cmd, the reverse of how the brief and their map write it. Almost certainly irrelevant to the CGI, but worth knowing if you ever byte-compare a capture. param/param2 are URL-encoded JSON produced by JSON.stringify. All three pages set $.ajaxSetup({cache:false, async:true, timeout:2000}) at file top, overriding the global async:false from js/utils.js - a 2 s timeout is tight for a slow sensor.

CREDENTIALS. username and password come from cookies, defaulting to the literals "admin"/"123456" if either cookie is absent (`var e="admin",h="123456", k=cookie match username, r=cookie match password; null!=k&&null!=r&&(e=k[2],h=r[2])` - image_setting3.js:1 byte ~3550, same pattern in the other two). view/login.html:47-48 sets `document.cookie="username="+username` and `document.cookie="password="+sha1_result`, and line 27 sets `document.cookie="temporary="+sha1_result` - the same value. So the established fact is confirmed: the `password` query parameter is the HmacSHA1 token, not the plaintext password.

ENUM SEMANTICS ARE NOT RECOVERABLE. Almost every option label in the image pages is an empty element carrying only an icbs_lang="TagNNNN" attribute; js/language.js is only the loader (LM.SrcPath / LM.GetCookie / etc.) and the actual string tables are not present in the mirror. So I can state the numeric domains with confidence but NOT what day_night_mode 0/1/2/3, wdr_enable 0/1/2, lamp_mode 0/1/2, day_night_switch 0/1, night_fps_select 0-3, day_night_lux 1-5, rotation 0-3 or the two *_brightness levels actually MEAN. Anywhere their map implied a meaning beyond the number, treat it as unverified. Only tv_standard (literal "PAL(50Hz)"/"NTSC(60Hz)" text), ircut_delay (literal 0..10, suffixed by Tag1814 - a unit, probably seconds, unconfirmed) and exposure (literal 1/N shutter fractions) carry self-describing labels.

CROSS-PAGE UI-CONTROL DIFFERENCES worth knowing. On image_setting.html mirror/flip/nr_enable/backlight_mode/anti_flicker_enable/ldc_enable are CHECKBOXES (html lines 188-208, read via .checked -> 1/0); on image_setting3.html mirror/flip/ldc_enable are SELECTS with values 0/1 (html 57/65/124, read via parseInt($.val())). image_setting.html also has #face_mode and #smart_face_mode checkboxes (html 212/216) that the JS IGNORES - both keys are hardcoded to 0 in every set on every page. image_setting3 has only 5 sliders (brightness, contrast, saturation, sharpness, max_led_brightness); there is no drc_strenght slider and param_change has no `case 4`, so set_single index 4 is never emitted from that page. image_setting2 renders only the max_led_brightness slider, so only index 5.

SIDE-EFFECT COUPLING ON SAVE. On image_setting2 and image_setting3 the Save button (window.sava_image) fires TWO writes: do_sava_image() then, only if #night_mode_ui is not display:none, do_sava_night_mode() which is the lamp_panel set. A client replicating the UI must decide whether to send both. Likewise Restore-Defaults (do_default) on those two pages issues lamp_panel default FIRST and only calls image default from inside its success callback, then reads lamp_mode/day_night_switch out of the LAMP_PANEL response - so the two are ordered and dependent.

CONDITIONAL SUPPRESSION OF THE SET. On image_setting.js the entire $.get for action=set sits inside the false-branch of a ternary guarded by check_schedule(day_begin_secs, day_end_secs), which returns 0 when (begin!=0||end!=0) && begin>=end. If that fires, the page toasts Tag2324 and NO request is sent at all. image_setting2 and image_setting3 define check_schedule but never call it - they always send.

ADJACENT, NOT-IMAGE CALLS SEEN ON THESE PAGES (listed for completeness, belongs to other analysts' subsystems, not enumerated as endpoints below): cmd=rtsp&action=get in image_setting.js:1 byte 3355 (reads .status, .auth_enable, .port to build the rtsp://host:port/stream1 preview URL); cmd=device&action=get in image_setting2.js:1 byte 8219 and image_setting3.js:1 byte 14918 (reads .status, .system_function bitmask, .cpu_type, .chn_num - the bits used on the image pages are 256 dual-lamp, 2048 show #ldc_ui, 131072 add wdr options 2/3, 262144 rebuild wdr options 0/1, 524288 alternate lamp_mode labels, 1048576 show #backlight_rotation).

RESPONSE PARSING IS LOOSE EVERYWHERE. get/default handlers test `"error"!=response.status`; set handlers test `"ok"==response.status`; set_single and lamp_panel set IGNORE the body completely. jQuery infers JSON from Content-Type, so the firmware must be returning application/json. A .fail() on every call distinguishes textStatus=="timeout" (Tag006) from everything else (Tag005).

### network  (14)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `http` | `default` | read | - | likely |
| `http` | `get` | read | - | certain |
| `http` | `set` | write | param | certain |
| `net` | `default` | read | - | likely |
| `net` | `get` | read | - | certain |
| `net` | `set` | write | param | certain |
| `onvif` | `default` | read | - | likely |
| `onvif` | `get` | read | - | certain |
| `onvif` | `set` | write | param | certain |
| `p2p` | `get` | read | - | certain |
| `p2p` | `set` | write | param | certain |
| `rtsp` | `default` | read | - | likely |
| `rtsp` | `get` | read | - | certain |
| `rtsp` | `set` | write | param | certain |

CORRECTIONS TO THE MAP I WAS GIVEN (their visible portion was truncated mid cmd=rtsp&action=get, so items marked "missing" may exist in the cut-off tail):

1. MISSING - cmd=rtsp&action=set sends THREE keys, not two. `a.enable=1` is hard-coded alongside auth_enable and port (js/rtsp_setting.js:1). Omitting it may well disable RTSP. Their param list for the analogous calls showed no sign of it.
2. MISSING - cmd=rtsp&action=get has a SECOND call site, js/image_setting.js:1 (byte 3348), and it is the only place in the mirror that shows the stream URL layout: `rtsp://[user:pass@]<host>:<port>/stream1`. That is the single most useful fact in this subsystem for a Windows client and it is absent from their map.
3. MISSING - the whole cmd=onvif module (get/set/default), if their tail did not carry it. Its port field is hidden in the UI (view/onvif_setting.html:27 style="display:none") and must be read before any write.
4. OVERSTATED - they marked p2p_id in the cmd=net&action=get response as "certain". It is not. view/network_setting.html has NO element with id="p2p_id" (the whole page is lines 19-81 and contains net_type, check_qwt, ipadative_mode, dhcp_mode, address, mask, gateway, dns_server, mac_addr only). `$("#p2p_id").val(b.p2p_id)` is a no-op left over from another page revision. Downgraded: the field may or may not exist in the response. Use cmd=p2p&action=get for the P2P id.
5. INCOMPLETE - they listed the `_` cache-buster only on cmd=net&action=get. `$.ajaxSetup({cache:!1})` is set at the top of every one of these files, so `_` is appended to ALL fourteen calls. It is also jQuery 1.10.2's incrementing `ajax_nonce` (seeded once from Date.now()), not a fresh epoch-ms per request. A native client can omit it entirely.
6. MISSING CONSTRAINT on cmd=net&action=set - `check_lan_valid()` gates the save UNCONDITIONALLY, including in DHCP mode. It returns 1 (the only accepted result) when the address is outside RFC1918 space, or when the address is RFC1918 AND (addr & mask) == (gateway & mask). It returns 0 - blocking the save - when the address is RFC1918 and the gateway is off-subnet, or when the address ends in .1 and equals the gateway. This is client-side only, so a native client can ignore it, but it explains why the web UI refuses some otherwise valid combinations.
7. PARAM ORDER - the settings pages emit `action` BEFORE `cmd` (`{action:"set",cmd:"net",...}`), whereas login.html emits cmd first. So the real URL is `/cgi-bin/web.cgi?action=set&cmd=net&username=..&password=..&param=..&_=..`. Combined with the verified login call, this proves the CGI is param-order-insensitive.
8. AUTH - correction to the established facts: the `temporary` cookie almost certainly does NOT authenticate web.cgi. view/login.html:27 does `document.cookie = "temporary=" + sha1_result` with no `path` attribute, and the page is served from /view/login.html (index.html redirects to view/login.html), so per RFC 6265 default-path rules these cookies scope to /view and would not accompany a request to /cgi-bin/web.cgi. Same for `username`, `password`, `expires`, `rtsp_username`, `rtsp_password` (view/login.html:45-50) - they are read back by same-directory JS, which is all the pages need. The `password` QUERY PARAMETER is what carries the token. A native client should therefore need no cookie jar. Flagging as high-confidence reasoning, not a live observation.

OTHER STRUCTURAL FACTS

- Response envelope: for every network cmd the JS reads fields at the TOP LEVEL, never under "data". The login response `{"status":"ok","data":""}` shows a `data` key exists on cmd=account&action=check, but no network handler touches one. Whether the firmware additionally nests a copy under `data` is not determinable.
- Content type: every handler treats the first callback arg as an object (a.port, b.iface). jQuery 1.10.2 with no dataType only produces an object when the server sends a JSON content-type, so the firmware must be returning application/json.
- Auth fallbacks: each page does `var c="admin",d="123456"; ...cookie match...; null!=e&&null!=f&&(c=e[2],d=f[2])`. Both cookies must be present or BOTH literals are used - and the literal "123456" is the plaintext password, not a token, so an unauthenticated page issues requests that cannot authenticate. Not a usable bypass.
- Session expiry is purely client-side: an `expires` cookie holding now+1800000 ms, re-stamped in every page's window.onload; if stale the page bounces to login.html. The server enforces nothing via that cookie.
- P2P availability gate (not itself a network cmd, so listed here rather than as an endpoint): view/left_menu.html:151-155 calls `$.get("/cgi-bin/web.cgi",{"action":"get","cmd":"device","username":username,"password":pwd})` and reads a bitmask, `cloud_support = data.system_function & (0x1 << 15)` (left_menu.html:169). Only then does the Cloud menu entry appear (line 228-230). So bit 15 of system_function tells a client whether cmd=p2p exists at all; bit 6 is gb28181. Note that this call passes the hard-coded `username="admin"`/`pwd="123456"` (left_menu.html:146-147) and never reads the cookies - so on this device that call either succeeds unauthenticated or the whole capability gate silently fails and every optional menu item stays hidden. Worth testing before relying on the bitmask.
- No cmd=p2p&action=default exists. The cloud page has only Refresh and Save; net/http/rtsp/onvif each have get/set/default.
- No WiFi endpoint is derivable. view/left_menu.html:303 has the wifi_setting.html menu item COMMENTED OUT and wifi_setting.html is not in the mirror, even though network_setting.js branches on iface=="wlan0" and force-disables the dhcp_mode select for it. Any WiFi cmd is unknown.
- All Tag#### labels are unresolvable: js/language.js:1 loads the dictionary from "/js/language/"+lang+".js" (with /js2/ fallbacks) and no language directory was mirrored. This is why dhcp_mode 1-6 and qwt_ip_adaptive_mode 0-7 have no derivable meanings - do not let anyone guess them.
- Live preview transport, for completeness (not web.cgi): js/player.js:1 and js/image_setting3.js:1 both build `ws://<host>:9101` for the browser decoder, using the rtsp_username/rtsp_password cookies and falling back to admin/123456. Port 9101 is hard-coded and is not configurable through any endpoint in this mirror.
- Timeouts per page, which hint at firmware response latency: cloud 2000 ms, net/http/rtsp 4000 ms, onvif 6000 ms.
- Subsystem boundary: the web UI's "Network" menu (view/left_menu.html:300-311) also contains ftp_setting, email_setting and alarm_center_setting, and a separate "Platform" menu holds gb28181 and cloud_setting. I treated cmd=ftp, cmd=alarm_out (email) , cmd=alarm_center and cmd=gb28181 as belonging to the alarm/notification subsystem and did not map them; cmd=p2p I did map since it was in my assigned scope. If nobody else has gb28181, it is 5 calls in js/gb28181_setting.js (get, set, default, and get_status twice) and is genuinely network-shaped.

### services  (27)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `alarm_center` | `default` | read | - | unsure |
| `alarm_center` | `get` | read | - | certain |
| `alarm_center` | `set` | write | param | certain |
| `alarm_center` | `test` | action | - | certain |
| `alarm_out` | `default` | read | - | unsure |
| `alarm_out` | `get` | read | - | certain |
| `alarm_out` | `set` | write | param | certain |
| `alarm_out` | `test_email` | action | - | certain |
| `ftp` | `default` | read | - | unsure |
| `ftp` | `get` | read | - | certain |
| `ftp` | `set` | write | param | certain |
| `ftp` | `test` | action | - | certain |
| `gb28181` | `default` | read | - | likely |
| `gb28181` | `get` | read | - | certain |
| `gb28181` | `get_status` | read | - | certain |
| `gb28181` | `set` | write | param | certain |
| `http` | `default` | read | - | likely |
| `http` | `get` | read | - | certain |
| `http` | `set` | write | param | certain |
| `onvif` | `default` | read | - | likely |
| `onvif` | `get` | read | - | certain |
| `onvif` | `set` | write | param | certain |
| `p2p` | `get` | read | - | certain |
| `p2p` | `set` | write | param | certain |
| `rtsp` | `default` | read | - | likely |
| `rtsp` | `get` | read | - | certain |
| `rtsp` | `set` | write | param | certain |

SCOPE: I define \"services\" as the eight modules under the web UI's Service/Platform menus (view/left_menu.html:304-318): rtsp, onvif, http, alarm_out (the e-mail page), ftp, alarm_center, gb28181, p2p. 27 distinct cmd/action pairs, listed above. I verified this is exhaustive by extracting every `web.cgi",{...}` literal in js/ and view/ and every multi-line $.get in the HTML (config.html:31, config.html:89, left_menu.html:151, login.html:35, login.html:127 - none of those five touch a services module).

=== CORRECTIONS TO THE PRIOR MAP ===
1. MISSED CALL SITE. cmd=rtsp&action=get is issued from TWO files, not one. The second is js/image_setting.js:1, and it is the more valuable one: it shows the RTSP URL the firmware expects a client to build - rtsp://[user:pass@]<host>:<port>/stream1, with the port taken from the get response and credentials embedded only when auth_enable==1. Its hard-coded pre-fetch fallback is port 5554 (`y="rtsp://"+m+":5554/stream1"`), which is a strong hint that 5554, not 554, is this firmware's factory RTSP port. /stream1 is the only stream path anywhere in the mirror.
2. NO cmd="email" EXISTS. The e-mail settings page drives cmd=alarm_out (get / set / default / test_email). If the prior map listed a cmd=email module, that is invented. Also, the test verb there is test_email, NOT test - it is the only non-{get,set,default,test,get_status} verb in the entire mirror.
3. THREE "default" ACTIONS ARE UNREACHABLE DEAD CODE, downgraded from likely to unsure: ftp/default, alarm_out/default and alarm_center/default. Their do_default/get_default functions are defined in the JS but nothing calls them - those three HTML pages carry a Test button and a Save button only, no Default (Tag008) button (view/ftp_setting.html:58-61, view/email_setting.html:77-80, view/alarm_center_setting.html:42-45). They are boilerplate copied from the rtsp/onvif/http pages and have never been exercised against this firmware, so a client should not assume the server implements them. By contrast rtsp/onvif/http/gb28181 DO have live Default buttons (view/rtsp_setting.html:31, view/onvif_setting.html:34, view/http_setting.html:26, view/gb28181.html:123) - those four stay at "likely".
4. "kind" FOR EVERY default IS UNPROVABLE. The prior map's "read" is a reasonable reading (the handler only repopulates the form; the user must still press Save) but no file proves the device does not also persist. Never state it as certain.
5. get/default SUCCESS VALUES ARE NOT PROVEN. Every get and default handler tests `status != "error"`; only set/test handlers test `status == "ok"`. So {"status":"ok"} is proven for writes and NOT proven for reads - a read may well return some other non-"error" status. Any claim that reads return "ok" is unsupported.
6. HTTP PORT-CHANGE BEHAVIOUR was overstated as "the request usually FAILS/times out". Both paths exist in the source: the normal success callback fires on {status:"ok"}, and the fail() handler additionally reinterprets a dropped connection as success whenever location.port != the new port. Correct client rule: accept EITHER a JSON body OR a dead socket, then reconnect on the new port.
7. trans_stream (gb28181) IS NOT A CODEC. The HTML comment "0:H264,1:H265,2:JPEG,3;MJPEG" at view/gb28181.html:75 is stale - it is pasted verbatim again at line 83 above the plainly-UDP/TCP protocol select. The option labels Tag1108/Tag1109 are the same pair used for the #record_stream main/sub-stream picker (view/storage_setting.html:45-46), so trans_stream is the stream selector. Marked likely, not certain.
8. FTP mode 0/1: the prior map was right to flag this as unprovable, and right about why - the tag tables are not in the mirror. js/language.js:1 (LM.AddScript) loads them from /js/language/<lang>.js, /js2/<lang>.js or /js2/language/<lang>.js; none of those directories exists on disk. So every TagNNNN label in this report is an identifier only. The two mappings that ARE source-proven are the ones written as literals in the HTML: alarm_center protocol 0=UDP/1=TCP and gb28181 trans_protocol 0=UDP/1=TCP, plus the email_crypto values "none"/"ssl"/"tls".

=== STRUCTURAL FACTS ===
TRANSPORT. Every call in the mirror is jQuery $.get -> HTTP GET with a query string. There is not one $.post, $.ajax or type:"POST" anywhere outside jquery-1.10.2.min.js and the unused jquery-form.js plugin. Param order follows JS object literal order: action, cmd, username, password, param. (cmd=ptz is the one module in the whole firmware that writes cmd before action - irrelevant to services, but it shows the CGI does not care about ordering.) `param` is the JSON body, URL-encoded by jQuery. No services call ever uses `param2` - that second slot is only used by the per-channel/per-stream modules (image, osd, privacy, snapshot_res, video, lamp_panel, stream_ability).

AUTH - one correction to the established facts. The `temporary` cookie is not what the settings pages consume. At view/login.html:27-32 the login page sets `document.cookie="temporary="+sha1_result` purely to force the CryptoJS WordArray through the browser's cookie jar and get a lowercase hex string back out; it then copies that into the real cookies at login.html:46-50: username, password (=the token), rtsp_username, rtsp_password (=the PLAINTEXT password, used for the RTSP URL), plus expires. Every services page then reads cookies `username` and `password` and passes them as the query parameters. Same pattern after a password change at js/user_setting.js:1. So: send the HmacSHA1 token as the `password` query parameter; a `temporary` cookie is not required by anything except the login page's own string conversion, and no code sets a Cookie header deliberately - the browser just sends whatever it has.
AUTH FALLBACK. Every services page opens with `var c="admin",d="123456"` and only overwrites both if the username AND password cookies are both present. So an unauthenticated client that sends nothing gets username=admin&password=123456 - a literal plaintext password in the token slot. Whether the CGI accepts that is not decidable from these files, but it is worth probing once, because js/hardware_config.js:1 issues `{action:"get",cmd:"hardware_config"}` and `{action:"get",cmd:"language"}` with NO username or password at all - proof that at least some cmds on this firmware need no credentials.

SESSION. There is no server-side session or Session-Id header (contrast the Vatilon H82 client). The only session state is a client-side cookie `expires` = now+1800000 ms, rewritten by each page's window.onload; if it is missing or stale the page bounces to login.html. A native client can ignore it entirely - it is never sent as a parameter and never validated by the CGI.

RESPONSE PARSING. dataType is never specified, so jQuery infers it from Content-Type. All handlers use object property access (a.status, a.port), so the CGI must be returning application/json. If a client sees a raw string it is a Content-Type problem, not a shape problem.

TIMEOUTS the UI itself allows, useful for calibrating a client: rtsp/http/ftp/alarm_out/alarm_center 4000 ms, onvif 6000 ms, gb28181 and p2p 2000 ms. Note the FTP and e-mail Test calls run under the same 4 s budget while doing real outbound network I/O, so a timeout there is expected behaviour, not a missing endpoint.

FEATURE GATING. Which service pages exist is decided client-side from a bitmask in the cmd=device get response (view/left_menu.html:151-231): gb28181 = system_function & (1<<6), email = & (1<<9), cloud/P2P = & (1<<15). js/hardware_config.js:1 corroborates 1<<6 and 1<<15 and adds 1<<16 = onvif_image_support, and exposes a parallel `system_function_support` mask saying which bits are even settable. rtsp, onvif, http, ftp and alarm_center are ungated - always present. A client should read cmd=device first if it wants to know whether gb28181/email/p2p are meaningful on a given unit.

MINOR SOURCE BUG worth knowing if you ever drive the real page: js/alarm_center_setting.js:1 alarm_port_check() writes its else-branch to element id "alarm_port_port_error", which does not exist in view/alarm_center_setting.html (the real id is "alarm_port_error"), so clearing a port error throws a TypeError. Client-side only; it does not affect the API.

### storage-osd-audio  (19)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `audio` | `get_input` | read | - | certain |
| `audio` | `get_output` | read | - | certain |
| `audio` | `input_default` | read | - | likely |
| `audio` | `output_default` | read | - | likely |
| `audio` | `set_input` | write | param | certain |
| `audio` | `set_output` | write | param | certain |
| `device` | `get` | read | - | certain |
| `osd` | `default` | read | param2 | likely |
| `osd` | `get` | read | param2 | certain |
| `osd` | `set` | write | param2, param | certain |
| `privacy` | `default` | read | param2 | likely |
| `privacy` | `get` | read | param2 | certain |
| `privacy` | `set` | write | param2, param | certain |
| `storage` | `format` | action | - | certain |
| `storage` | `get_disk_info` | read | - | certain |
| `storage` | `get_format_status` | read | - | certain |
| `storage` | `get_record_info` | read | - | certain |
| `storage` | `record_default` | read | - | likely |
| `storage` | `set_record_info` | write | param | certain |

SCOPE / COMPLETENESS. I re-derived this from scratch by beautifying the four minified sources and reading them end to end, then inventoried `cmd:"..."` across all 40 js and 36 html files. cmd "storage", "osd", "audio" and "privacy" occur in exactly one file each (js/storage_setting.js, js/osd_setting.js, js/audio_setting.js, js/region_cover.js) - nowhere else in the mirror - so there are no call sites outside these four pages. 18 in-subsystem calls total, plus the two cmd=device calls the OSD and privacy pages make on load. Every call is `$.get`; there is no $.post, no $.ajax, and no explicit type/method anywhere in these files, so GET is certain, not assumed. Note that the map I was given was truncated mid-sentence inside its cmd=osd&action=set entry, so I could only check its visible portion; everything above is independently derived rather than diffed.

CORRECTIONS TO THE PRIOR MAP (visible portion only):
1. get_format_status - their "status: int, numeric not string" marked certain is not supported. The tests are `0==b.status` / `2==b.status`, loose equality, which accept "0" and "2" as strings. Downgraded. The numeric claim IS defensible for get_disk_info, whose `switch(a.status)` uses strict equality - but even there a string status would merely render no label while still passing `"error"!=d.status`.
2. get_disk_info status 0 - "no card" is an interpretation of an unresolvable Tag1620, stated as fact. All the source supports is: status 0 suppresses the free/total line and is the state in which the Format button is refused.
3. get_record_info stream - they hedged "most likely 0=main, 1=sub". This is actually resolvable from the mirror: js/player.js:1 (char 7070) sets `document.getElementById("main_stream").title=LM.LD.Tag1108` and `...("sub_stream").title=LM.LD.Tag1109`, so Tag1108=main, Tag1109=sub, and record_stream 0=main / 1=sub is established. Their cited evidence (gb28181.html) is the weaker source - that select is #trans_stream and carries a leftover `<!--0:H264,1:H265,2:JPEG,3;MJPEG-->` comment, so it argues against, not for, a main/sub reading.
4. Character offsets in their source citations are consistently ~25-27 too high (e.g. set_record_info 9645 vs the actual 9618, get_disk_info 14106 vs 14079, format 10062 vs 10035, get_format_status 10311 vs 10284, record_default 10829 vs 10802, osd get 4881 vs 4854). They appear to have measured to the `{` of the data object rather than the `$`. All offsets above are the byte index of the `$`/`b` that starts the call, verified programmatically.
5. set_record_info - their precondition list omits which message fires on a bad schedule: it is Tag2324 (char 9559), distinct from the Tag009 used for the pack-time check.
6. cmd=osd&action=default is missing from the portion of their map I saw. If their osd section really ended at get and set, that is a missed endpoint. The same "default" verb (not "get_default", not "<module>_default") applies to cmd=privacy. Note the inconsistency across this subsystem: storage uses "record_default", audio uses "input_default"/"output_default", osd and privacy use plain "default".
7. Their per-key JSON ordering claims are accurate but are only an artefact of how the objects are constructed; nothing shows the device is order-sensitive. Treat key order as descriptive, not a requirement.

AUTH / SESSION. Every page in this subsystem derives credentials identically: `p="admin", t="123456"` then `document.cookie.match(/(^| )username=([^;]*)(;|$)/)` and the same for `password`; only if BOTH match does it use the cookie values. So the fallback is the literal admin/123456 pair, and username/password are always sent as query parameters regardless. view/login.html confirms the established facts and adds a detail worth knowing: `var encry_password = hex_md5(pwd); var sha1_result = CryptoJS.HmacSHA1(encry_password, username);` then it writes THREE cookies from that one value - `temporary=<token>`, `password=<token>`, `username=<name>` (login.html:24,25,27,47,48) - plus `rtsp_username` and, notably, `rtsp_password=<plaintext password>` for the WebSocket player. So cookie "password" and cookie "temporary" hold the same HmacSHA1 token; the subsystem pages read "password". For a native client, sending the username/password query parameters is sufficient - nothing in these four files depends on a cookie being present on the wire.
Session guard: each page's window.onload reads cookie `expires`, redirects to login.html if absent or in the past, and rewrites it to now+1 800 000 ms (30 min). This is purely client-side; no evidence of server-side session state.

RESPONSE CONTRACT. Reads test `"error" != resp.status` and treat anything else as success. Writes test `resp.status == "ok"`. So `status` is polymorphic: the string "error", the string "ok", or an integer (disk state, format progress) depending on the call. A client should parse it as a JSON value, not as an int or a string.

ENCODING. param and param2 are ordinary query parameters whose value is `JSON.stringify(obj)`; jQuery percent-encodes them. Where both appear (osd set, privacy set) param2 is emitted first. param2 is the channel/sensor selector and exists only for cmd=osd and cmd=privacy - cmd=storage and cmd=audio have no channel concept at all on this firmware.

TIMEOUTS. js/utils.js runs first and globally sets `$.ajaxSetup({async:!1})`; each page then overrides with `cache:false, async:true` and a timeout - 2000 ms in storage_setting.js, osd_setting.js and region_cover.js, but 6000 ms in audio_setting.js. A native client should not copy the 2 s figure for anything that touches the SD card.

GOTCHAS AND FIRMWARE-UI BUGS WORTH NOT COPYING:
- cmd=storage&action=format inherits the 2000 ms timeout and its .fail handler re-applies `$.ajaxSetup({timeout:2E3})` - restoring a value nothing ever raised. As shipped, a format that takes longer than 2 s always surfaces as a client-side "timeout" (Tag006) even though the device is working; the real result only arrives via get_format_status polling. Give this call a long timeout in the app.
- js/osd_setting.js:1 (char 8404): `function change_channel(a){_reload_osd();0==a?_change_channel_stream(0,1):_change_channel_stream(1,1)}` - it reloads the OSD *before* g_channel is updated (so the first request after a channel switch carries the previous channel), and it clamps the channel to 0 or 1, so on a device with chn_num>2 the OSD page can never address channel 2+. The privacy page does it correctly (`window.sensor_change=function(a){channel=parseInt(a);...}` at char 5756). The app should just send param2 {"channel":n} directly.
- Audio volumes: `input_volume`/`output_volume` are module globals initialised to 0 and updated only from the layui slider `change` callback. The get_input/get_output handlers call `g.setValue(...)`/`h.setValue(...)`; whether layui's setValue fires `change` cannot be verified because js/layui was not mirrored. If it does not, a Save without touching the sliders would write volume 0 for both. Worth an explicit read-modify-write in the app rather than trusting the UI pattern.
- privacy get clamps a box to `offsetWidth-5-x` on read and privacy set adds 5 px back when the box is flush to the edge - a compensating pair, but it means a naive get→set round-trip through the page is not exactly idempotent.
- privacy responses with rect_num > 4 would crash the page (only #draw_box0..3 exist), so 4 masks is the practical maximum.
- audio set_input always sends sample_rate, bit_width and input_type even though those selects are rendered `disabled`; #sel-inputType is only unlocked by clicking the Tag2607 label 5+ times within 5 seconds (`window.check_click` at js/audio_setting.js:1 char 3774) - an easter-egg service gate, not an API constraint.

LANGUAGE DICTIONARY. Every Tag#### string is unresolvable from this mirror: view pages reference them via `icbs_lang` attributes and js/language.js loads the dictionary from a `/js/language/` directory that list_js.html shows exists on the device (dr-x, 111 bytes, alongside language.js) but which was not mirrored. So the semantics of record mode 0/2, time_fmt 0/1, and all OSD position values remain genuinely unknown. Two partial resolutions I could make from other mirrored files: Tag1108/Tag1109 = main/sub stream (js/player.js, above), and Tag2302..Tag2308 is a weekday sequence indexed 0..6 - view/time_setting.html:159-167 uses exactly those seven tags as the option values 0..6 of a DST day-of-week select (#day0/#day1). That confirms schedule[0..6] are weekday rows in the firmware's own 0..6 weekday order, but does not name them; index 0 being Sunday is a convention, not something the mirror proves. schedule[7] is the row rendered above the weekday rows and labelled Tag2319 (an "every day"/"all week" row by position, unconfirmed).

ADJACENT ENDPOINT, NOT web.cgi. The privacy page's still image comes from a second CGI: `document.getElementById("region_cover").src="http://"+window.location.host+"/cgi-bin/snapshot.cgi?channel="+channel+"&t="+Math.random()` (js/region_cover.js:1 char 4137). No credentials in the URL - it relies on the browser's cookies. The same endpoint is used by motion_detect.js, person_detect.js and face_invasion_detect.js. Useful to the app for drawing masks without decoding the video stream, and it is re-fetched automatically after a successful privacy set.

### video-encoder  (13)

| module | verb | kind | slots | confidence |
|---|---|---|---|---|
| `image` | `get` | read | param2 | certain |
| `snapshot_res` | `ability` | read | param2 | certain |
| `snapshot_res` | `default` | read | param2 | certain |
| `snapshot_res` | `get` | read | param2 | certain |
| `snapshot_res` | `set` | write | param2, param | certain |
| `stream_ability` | `get_main` | read | param2 | certain |
| `stream_ability` | `get_sub` | read | param2 | certain |
| `video` | `get_main` | read | param2 | certain |
| `video` | `get_sub` | read | param2 | certain |
| `video` | `main_default` | read | param2 | certain |
| `video` | `set_main` | write | param2, param | certain |
| `video` | `set_sub` | write | param2, param | certain |
| `video` | `sub_default` | read | param2 | certain |

CORRECTIONS TO THE MAP UNDER REVIEW

1. MISSED CALLS (4). Their map lists snapshot_res/get and stops. The mirror actually has four snapshot_res verbs - get, ability, set, default (js/snapshot_res_setting.js bytes 1941 / 2118 / 684 / 1131) - plus a cmd=image&action=get issued by the encoder page itself (js/video_setting.js byte 6897). Note the snapshot capability verb is the bare literal "ability", NOT a get_main/get_sub pair and NOT a separate cmd; and its reset verb is bare "default", not main_default/sub_default. Anyone extrapolating the video module's naming onto snapshot_res will guess wrong.

2. WRONG PARAMETER DOMAIN. Their quality note ("Six options, Tag5031..Tag5036, 1..6") is right for the VIDEO page but must not be carried onto snapshot_res: view/snapshot_res_setting.html:36-38 gives only three options, values 1|2|3, labels Tag2443/2444/2445. Both pages use the id #sel-quality and the Tag5030 caption, which is exactly how that mistake gets made.

3. TAG MISATTRIBUTION. They wrote that at rc_mode 1 the field "is relabelled Tag1811" as if Tag1811 were option 1's own label. It is not. The <option>s are Tag1806 (value 0) and Tag1807 (value 1) at view/video_setting.html:58-59; Tag1811 is only ever the BITRATE FIELD caption for mode 1, set by video_check_rc_mode. Their CBR/VBR reading stays a guess - correctly caveated, and I confirm the language pack (/js/language/<lang>.js, loaded by LM.AddScript in js/language.js) is absent from the mirror, so no Tag text and no LM.RESOLUTION[] label map can be verified. Same caveat applies to the quality direction on both pages.

4. OVERSTATED "certain" ON RESPONSE SHAPE. Every response entry in their map is asserted flatly. What the source actually proves is the set of field NAMES the JS reads and that they sit at top level (no "data" wrapper) - the TYPES are inferred from use, and nothing rules out extra fields (the login reply carries a "data" key that this page would simply ignore). I have kept endpoint confidence at "certain" because cmd/action/params are literal in the source, and moved the hedging into each responseShape.

5. BYTE OFFSETS. Theirs are consistently 19 bytes high (3499/3994/5029/5195/1960 vs the actual 3480/3975/5010/5176/1941), probably a BOM/anchor difference. Both files are a single minified line, so "file:1" is right either way; corrected offsets are in each source field.

6. CONFIRMED AS THEY HAD IT. The get/set/default triple on cmd=video with the main/sub action split; stream_ability reusing the same get_main/get_sub action variable and being issued only as a nested follow-up; param2 = {"channel":N}, 0-based; venc_set bit0=H264 / bit1=H265; the eight-key all-or-nothing param object on set; chn_num living on the stream_ability response (the inner callback's `e` shadows the outer action string and IS the ability response, so `e.chn_num` is not the video record); stream_ability's status going unchecked; and *_default being a read that only populates the form.

STRUCTURAL NOTES FOR THE CLIENT

- Method: every call in this subsystem is jQuery $.get, i.e. HTTP GET with everything in the query string. There is no POST anywhere in either file (the three $.ajax hits are $.ajaxSetup). param and param2 are URL-encoded JSON blobs.
- Parameter ORDER as emitted: action, cmd, username, password, param2[, param] - jQuery.param preserves object key order, so the shipped UI sends action FIRST, unlike the cmd-first login call in the established facts. Given the firmware answers "param num error" on short calls, a count/order-sensitive parser is plausible; safest is to mirror the UI exactly. Reads send 5 params, writes send 6.
- Auth: username/password come from the "username"/"password" cookies; view/login.html sets password=<HmacSHA1(hex_md5(pwd), username)> and temporary=<same value>, confirming the established facts. Both files fall back to hard-coded "admin"/"123456" when the cookies are missing - and view/left_menu.html (byte ~2100, window.get_info, cmd=device&action=get) NEVER reads the cookies at all, always sending admin/123456. That is a firmware quirk worth knowing about, not a pattern to copy.
- FRAMERATE CLAMP - the biggest practical gotcha. The UI does not use max_framerate as the ceiling. js/video_setting.js:1 computes `k=30==b.max_framerate?0==w?25:30:60==b.max_framerate?0==w?50:60:b.max_framerate` where w=tv_standard from cmd=image. So on PAL (tv_standard 0) a device reporting max_framerate 30 is clamped to 25 and one reporting 60 is clamped to 50; on NTSC the reported value stands; any other max_framerate passes through unclamped. This clamp is client-side only - nothing proves the device rejects 30fps on PAL. Worse, get_image_param() runs under $.ajaxSetup({async:!0}) with a 2000ms timeout and its result is only consumed by the LATER stream_ability callback, so on a slow reply the page silently clamps as if PAL (w defaults to 0). A native client should read tv_standard explicitly rather than inherit this race.
- Per-stream limits: main and sub have independent min/max for framerate, bitrate and gop; the page caches them in parallel arrays indexed by stream. Do not read ability once and apply it to both.
- UI-only input caps that are NOT device limits: framerate and gop inputs are maxlength="2", bitrate is maxlength="4". A device advertising max_gop 100+ or max_bitrate 10000+ cannot be driven to those values from the web UI; a native client is not bound by that.
- Resolution wire format: the <select> value is the string "W*H" (asterisk, not "x"), split on save. Option LABELS come from LM.RESOLUTION["1920*1080"] in the missing language pack, so the mirror gives you the width/height pairs but no display names.
- Capability gate: the snapshot page is only reachable when bit 14 of `system_function` is set - `snapshot_res_support = data.system_function & (0x1 << 14)` in view/left_menu.html:168, from cmd=device&action=get. On a camera without that bit the snapshot_res endpoints may not exist at all. (Same reply gates image via bit 13 and cpu_type, which selects between image_setting.html and image_setting3.html.)
- KNOWN BUG in the mirror: js/snapshot_res_setting.js window.sensor_change calls `do_stream_change(0)`, a function that exists only in video_setting.js. On a multi-sensor camera the snapshot page's sensor dropdown therefore throws ReferenceError and never reloads - it updates the `channel` global but the form still shows the previous sensor's values. Do not replicate; a client should re-issue snapshot_res/get after changing channel.
- Adjacent but out of scope: cmd=rtsp (get/set/default, js/rtsp_setting.js) governs the RTSP port and auth_enable and takes NO param2 - it is not channel-scoped, unlike everything in this subsystem. Flagging it because it is the other half of "how do I get a stream out of this camera", and because its param shape breaks the param2-is-always-present assumption.

