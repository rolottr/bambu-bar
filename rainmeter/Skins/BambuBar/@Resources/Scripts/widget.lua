--[[ Bambu Bar desktop widget: all the skin's logic (task P2.4, clicks P2.5).

The app writes what to show (windows/docs/widget-state.md, contract v1); this script reads it once a second,
moves the countdown and progress between writes, ages a stale file to "offline", lays the widget out and
animates it. The skins (Small/Medium/Large) only declare meters; every text, position and colour is set here.

Two halves:
  * BB.*: pure functions (no SKIN or SELF), loaded and tested by rainmeter/tests with LuaJIT.
  * Initialize/Update/Frame/Spin/Hover: the Rainmeter glue.

Rainmeter 5.0 notes (checked against the 5.0.0.5293 source, github.com/rainmeter/rainmeter, and
https://www.rainmeter.net/changes-5.0/):
  * This file starts with a UTF-8 BOM: LuaScript.cpp treats a script as UTF-8 only when it has one, otherwise
    every string (Russian, "≈", "·") would be read as ANSI.
  * SKIN:Bang runs the bang at once (LuaBindingSkin.cpp calls ExecuteBang directly), so "!UpdateMeter" followed
    by meter:GetW()/GetH() measures text in the same call (MeterString::Update measures when the text changes).
  * SKIN:SetOption with an empty value deletes the option (Skin::SetOption), and a Shape meter stops reading
    ShapeN at the first missing one (MeterShape.cpp), so unused shape slots are set to "".
  * ActionTimer is built in ("Measure=ActionTimer"); "!CommandMeasure <name> "Execute N"/"Stop N"" still works.
]]

BB = {}

local floor, ceil, max, min, abs = math.floor, math.ceil, math.max, math.min, math.abs
local sin, cos, rad, deg, atan2, sqrt = math.sin, math.cos, math.rad, math.deg, math.atan2, math.sqrt
local fmt, gsub, find, sub = string.format, string.gsub, string.find, string.sub

-- ===================================================================================== constants

BB.MARGIN = 16 -- transparent margin around the card, for the shadow (mockup: Small window 212 x 212)
BB.SIZES = { Small = { w = 180, h = 180 }, Medium = { w = 380, h = 180 }, Large = { w = 380, h = 380 } }
BB.STALE_AFTER = 30 * 60

-- The Mac's widget palette (WidgetPalette in WidgetState.cs) and the mockup's tokens.
BB.C = {
	accent = "21CC6B", preparing = "5CA8FF", paused = "FFB83D", failed = "FF5C5C", finished = "6BDB9E",
	idle = "9EA8B3", secondary = "8E8E93", nozzle = "FF7A4D", bed = "FFB84D", chamber = "8CB3FF",
	text = "F2F2F5FF", sec = "EBEBF59E", tile = "FFFFFF12", track = "FFFFFF1A",
}

-- Card gradient and border, at rest and hovered (mockup .w / .w:hover).
BB.CARD = { top = { 0x29, 0x2B, 0x2E }, bottom = { 0x14, 0x17, 0x19 }, border = 0.08,
	hoverTop = { 0x31, 0x33, 0x37 }, hoverBottom = { 0x19, 0x1B, 0x1E }, hoverBorder = 0.22 }

-- Timings in ms (widget-state.md, "Motion and hover").
BB.T = { fadeOut = 150, fadeIn = 180, tween = 450, pulse = 700, art = 300, light = 400, hover = 140,
	spinTurn = 1100 }

-- Type, in CSS px as in the mockup; lh is the mockup's line-height. Rainmeter's FontSize is in points
-- (TextFormat.cpp multiplies it by 4/3), so px are converted with * 0.75.
BB.styles = {
	cap = { face = "text", px = 10.5, weight = 700, case = "Upper", ls = 0.4, lh = 1.25 },
	capTwo = { face = "text", px = 10.5, weight = 700, case = "Upper", ls = 0.2, lh = 1.2 },
	big = { face = "num", px = 20, weight = 600, lh = 1.15 },
	bigAgo = { face = "num", px = 18, weight = 600, lh = 1.15 },
	sub = { face = "text", px = 11, weight = 400, color = "sec", lh = 1.25 },
	subNum = { face = "num", px = 11, weight = 600, color = "sec", lh = 1.25 },
	reason = { face = "text", px = 13, weight = 600, lh = 1.2 },
	noLive = { face = "text", px = 12.5, weight = 600, lh = 1.2 },
	tempRow = { face = "num", px = 14, weight = 600, lh = 1.25 },
	spoolLine = { face = "text", px = 11, weight = 500, color = "sec", lh = 1.25 },
	footTitle = { face = "text", px = 13, weight = 600, lh = 1.25 },
	footDetail = { face = "num", px = 11, weight = 600, color = "sec", lh = 1.25 },
	footLine = { face = "text", px = 11, weight = 400, color = "sec", lh = 1.25 },
	dialPct = { face = "num", px = 18, weight = 700, lh = 1.1, pct = { px = 9.5, color = "sec" } },
	name = { face = "text", px = 12, weight = 600, color = "sec", lh = 1.25 },
	status = { face = "text", px = 11.5, weight = 600, lh = 1.25 },
	headline = { face = "text", px = 16, weight = 600, lh = 1.25 },
	pct = { face = "num", px = 32, weight = 600, lh = 1.08, pct = { px = 16, color = "sec" } },
	layers = { face = "num", px = 11.5, weight = 600, color = "sec", lh = 1.25 },
	sline = { face = "num", px = 11.5, weight = 600, lh = 1.25 },
	slineRight = { face = "num", px = 11.5, weight = 600, color = "sec", lh = 1.25 },
	line2 = { face = "text", px = 12, weight = 400, color = "sec", lh = 1.25 },
	tempInline = { face = "num", px = 11.5, weight = 600, lh = 1.25 },
	tileLabel = { face = "text", px = 11, weight = 500, color = "sec", lh = 1.25 },
	tileValue = { face = "num", px = 19, weight = 600, lh = 1.15 },
	tileTarget = { face = "num", px = 10.5, weight = 600, lh = 1.15 },
	amsTitle = { face = "text", px = 10.5, weight = 700, case = "Upper", ls = 0.4, color = "sec", lh = 1.25 },
	amsHum = { face = "text", px = 11, weight = 500, color = "sec", lh = 1.25 },
	spoolMat = { face = "text", px = 11, weight = 600, lh = 1.25 },
	spoolAmt = { face = "num", px = 10.5, weight = 600, color = "sec", lh = 1.25 },
	foot = { face = "text", px = 11.5, weight = 500, lh = 1.25 },
	footRight = { face = "text", px = 11.5, weight = 500, color = "sec", lh = 1.25 },
	info = { face = "text", px = 12, weight = 400, color = "sec", lh = 1.35 },
}

-- Glyphs in @Resources\Icons. stroke = the file has a group with id "s" drawn with a stroke.
BB.ICONS = {
	nozzle = "fire", bed = "heat", chamber = "temperature", fan = "fan", drop = "drop", check = "checkmark",
	cross = "dismiss", bang = "important", pause = "pause", warn = "warning", okCircle = "checkmark-circle",
	xCircle = "dismiss-circle", key = "key", wifiOff = "wifi-off", printer = "printer",
}
BB.STROKE_ICONS = { heat = true, printer = true }

-- ===================================================================================== small helpers

function BB.trim(s) return (gsub(s or "", "^%s+", ""):gsub("%s+$", "")) end

-- "{0} left" with "1h 02m" → "1h 02m left". Plain replacement: the arguments are never patterns.
function BB.fill(pattern, a, b)
	local out = pattern or ""
	local i = find(out, "{0}", 1, true)
	if i then out = sub(out, 1, i - 1) .. tostring(a) .. sub(out, i + 3) end
	if b ~= nil then
		local j = find(out, "{1}", 1, true)
		if j then out = sub(out, 1, j - 1) .. tostring(b) .. sub(out, j + 3) end
	end
	return out
end

-- The Mac's Format.duration from the contract's patterns.
function BB.duration(m, strings)
	strings = strings or {}
	if not m or m <= 0 then return strings.lessThanMinute or "<1m" end
	local h = floor(m / 60)
	if h == 0 then return BB.fill(strings.minutes or "{0}m", m) end
	if h >= 24 then return BB.fill(strings.daysHours or "{0}d {1}h", floor(h / 24), h % 24) end
	return BB.fill(strings.hoursMinutes or "{0}h {1}m", h, fmt("%02d", m % 60))
end

-- Minutes left: printing → ceil((endsAt − now) / 60), at least 0; paused → as written; else nil.
function BB.minutesLeft(s, now)
	if s.state == "printing" and s.endsAt then return max(0, ceil((s.endsAt - now) / 60)) end
	if s.state == "paused" then return s.remainingMinutes end
	return nil
end

-- The Mac's projectedProgress: 0–1, linear from progress at updatedAt to 1 at endsAt, capped at 0.99,
-- only while printing.
function BB.projectedProgress(s, now)
	local p = (s.progress or 0) / 100
	local u, e = s.updatedAt, s.endsAt
	if s.state ~= "printing" or not u or not e or e <= u then return p end
	local f = min(1, max(0, (now - u) / (e - u)))
	return min(0.99, p + (1 - p) * f)
end

-- "#RRGGBB" (or "RRGGBB") + alpha 0–1 → Rainmeter's "RRGGBBAA".
function BB.rgba(hex, a)
	hex = gsub(hex or "", "^#", "")
	if not find(hex, "^%x%x%x%x%x%x") then hex = BB.C.idle end
	return string.upper(sub(hex, 1, 6)) .. fmt("%02X", floor(max(0, min(1, a or 1)) * 255 + 0.5))
end

function BB.rgb(hex)
	hex = gsub(hex or "", "^#", "")
	if not find(hex, "^%x%x%x%x%x%x") then return 158, 168, 179 end
	return tonumber(sub(hex, 1, 2), 16), tonumber(sub(hex, 3, 4), 16), tonumber(sub(hex, 5, 6), 16)
end

function BB.luminance(hex)
	local r, g, b = BB.rgb(hex)
	return (0.299 * r + 0.587 * g + 0.114 * b) / 255
end

-- The spool's hub (the app's SpoolArt.Hub): the filament colour deepened (more for light ones), or lifted for
-- near-black filament, so the core reads as part of the spool rather than a black hole. RRGGBBFF.
function BB.hubColor(hex)
	local r, g, b = BB.rgb(hex)
	local l = BB.luminance(hex)
	local target, t = 0, 0.4
	if l < 0.16 then target, t = 255, 0.26 elseif l > 0.7 then t = 0.5 end
	local function mix(v) return floor(v + (target - v) * t + 0.5) end
	return fmt("%02X%02X%02XFF", mix(r), mix(g), mix(b))
end

-- Text from the state file can hold "#" or "[", which Rainmeter would read as variables when the option is
-- parsed. Character references ([\xHH], ConfigParser.cpp "Always process escaped character references") are
-- not parsed again.
function BB.esc(s)
	return (gsub(s or "", "[#%[]", function(c) return c == "#" and "[\\x23]" or "[\\x5B]" end))
end

function BB.num(s) -- "164°" → 164
	local n = s and string.match(s, "%-?%d+%.?%d*")
	return n and tonumber(n) or nil
end

local function n2(v) -- numbers in options: at most 2 decimals, no trailing zeros
	local s = fmt("%.2f", v)
	s = gsub(s, "0+$", "")
	return (gsub(s, "%.$", ""))
end
BB.n2 = n2

function BB.utf8len(s)
	local _, n = gsub(s or "", "[^\128-\191]", "")
	return n
end

-- ===================================================================================== state → model

-- json.null → nil, recursively.
function BB.clean(v, null)
	if type(v) ~= "table" then return v end
	for k, x in pairs(v) do
		if x == null then v[k] = nil else BB.clean(x, null) end
	end
	return v
end

-- What the widget shows at `now`: the file's fields plus the countdown, the projected progress and the
-- stale rule applied. Pure: the same file and clock give the same model.
function BB.model(s, now)
	local m = {}
	for k, v in pairs(s) do m[k] = v end
	m.strings = s.strings or {}
	m.temps = s.temps or {}
	m.spools = s.spools or {}
	m.motion = s.motion ~= false

	if (s.v or 1) > 1 then
		-- A newer app wrote this: the skins on disk are older than the app (the app copies them on update).
		m.link, m.live, m.state = "unconfigured", false, "idle"
		m.caption = "Update Bambu Bar"; m.headline = "Update Bambu Bar"; m.stateLine = ""; m.detail = ""
		m.temps, m.spools, m.ams, m.issue = {}, {}, nil, nil
	end

	-- What a click opens (contract: url; older files only had the serial).
	m.url = s.url or ((m.serial and m.serial ~= "") and ("bambubar://printer/" .. m.serial) or "bambubar://open")

	m.stale = (m.link == "online" or m.link == "connecting") and s.savedAt ~= nil
		and now - s.savedAt > BB.STALE_AFTER
	if m.stale then
		-- The app has stopped writing: show the printer offline (contract: "older than 30 min → offline").
		local o = s.offline or {}
		m.link, m.live, m.state, m.canceled = "offline", false, "idle", false
		m.caption = o.caption or m.strings.offline or "Offline"
		m.headline = o.headline or m.caption
		m.stateLine = o.stateLine or (s.lastJob and s.lastJob.name and BB.fill(m.strings.last or "{0}", s.lastJob.name)) or ""
		m.detail = o.detail or ""
		m.info = o.info
		m.stateColor = "#" .. BB.C.idle
		m.temps, m.spools, m.ams, m.issue, m.activeSpoolLine = {}, {}, nil, nil, nil
		m.artUnlit = nil
	end

	m.active = m.live and (m.state == "printing" or m.state == "preparing" or m.state == "paused")
	m.progress = m.live and BB.projectedProgress(m, now) or 0
	m.percent = floor(m.progress * 100 + 1e-9)
	m.minutesLeft = m.live and BB.minutesLeft(m, now) or nil
	m.countdown = m.minutesLeft and BB.duration(m.minutesLeft, m.strings) or nil
	if m.live and m.state == "printing" and m.countdown then
		m.stateLine = BB.fill(m.strings.left or "{0} left", m.countdown)
	end
	-- The Small widget writes "left" under the big countdown: the pattern without its argument, when the
	-- duration comes first ("{0} left"). "Noch {0}" has no word that reads alone after it, so none is shown
	-- unless the app sends one (strings.leftWord).
	local pattern = m.strings.left or "{0} left"
	m.leftWord = m.strings.leftWord
		or (find(pattern, "^%s*{0}") and BB.trim((gsub(pattern, "{0}", "")))) or nil

	-- detail is "Done 5:15 PM · 170/305" while printing; the Medium widget shows the finish time on its own.
	local first = string.match(m.detail or "", "^(.-) · ") or m.detail
	if m.live and m.state == "printing" and s.finish then
		m.finish = BB.fill(m.strings.done or "{0}", s.finish)
	elseif m.live and m.state == "printing" and first and first ~= "" and first ~= m.layers then
		m.finish = first
	end
	m.detailFirst = first

	for _, sp in ipairs(m.spools) do if sp.active then m.activeSpool = sp end end
	if m.live and m.state == "preparing" and m.temps[1] then
		local v, t = BB.num(m.temps[1].value), BB.num(m.temps[1].target)
		m.heat = (v and t and t > 0) and min(1, max(0, v / t)) or 0
	end
	m.captionColor = m.live and BB.rgba(m.stateColor, 1)
		or (m.link == "unauthorized" and BB.rgba(BB.C.paused, 1) or BB.C.sec)
	m.key = table.concat({ m.link or "", tostring(m.live), m.state or "", tostring(m.canceled) }, "/")
	return m
end

-- The value the Small dial's ring shows (it is not always the progress), and the Medium/Large bar.
function BB.dialValue(m, size)
	if not m.live then return 0 end
	if size == "Small" then
		if m.state == "preparing" then return m.heat or 0 end
		if m.state == "finished" then return 1 end
	end
	if m.state == "idle" then return 0 end
	return m.progress
end

-- What changed between two models, for the animations. Fires once per change: comparing a model with
-- itself (or with the next second's) returns no state change.
function BB.changes(prev, cur, size)
	local c = {}
	if not prev then c.first = true; return c end
	c.state = prev.key ~= cur.key
	c.finished = cur.live and cur.state == "finished" and not (prev.live and prev.state == "finished")
	local a, b = BB.dialValue(prev, size), BB.dialValue(cur, size)
	if abs(a - b) >= 0.02 or (c.state and a ~= b) then c.progress = { from = a, to = b } end
	c.art = BB.artTransition(prev, cur)
	return c
end

-- art → art crossfade: 400 ms when only the chamber light switched (one file's art is the other's artUnlit),
-- 300 ms otherwise (the part grew, a spool changed, the state changed).
function BB.artTransition(prev, cur)
	if prev.art == cur.art then return nil end
	local light = (prev.artUnlit ~= nil and prev.artUnlit == cur.art) or (cur.artUnlit ~= nil and cur.artUnlit == prev.art)
	return { from = prev.art, to = cur.art, ms = light and BB.T.light or BB.T.art, light = light }
end

-- ===================================================================================== easing / tweens

function BB.easeOut(t) t = 1 - t; return 1 - t * t * t end
function BB.smooth(t) return t * t * (3 - 2 * t) end

-- Tween state: { from, to, t0, ms, ease }. Value at clock `now` (seconds), and whether it is done.
function BB.tweenAt(tw, now)
	local k = tw.ms <= 0 and 1 or min(1, max(0, (now - tw.t0) * 1000 / tw.ms))
	local e = (tw.ease or BB.smooth)(k)
	return tw.from + (tw.to - tw.from) * e, k >= 1
end

-- ===================================================================================== shapes

local NONE = "" -- an unused ShapeN slot (deleted, which ends the meter's shape list)

-- An arc from the top of the circle, clockwise over `f` of a turn (Shape "Arc": sweep flag 0 = clockwise,
-- size flag 1 = large arc; MeterShape.cpp ReadFlagOrDefault).
function BB.arc(cx, cy, r, f, startDeg)
	startDeg = startDeg or 0
	local a0, a1 = rad(startDeg), rad(startDeg + f * 360)
	local x1, y1 = cx + r * sin(a0), cy - r * cos(a0)
	local x2, y2 = cx + r * sin(a1), cy - r * cos(a1)
	return fmt("Arc %s,%s,%s,%s,%s,%s,0,0,%d", n2(x1), n2(y1), n2(x2), n2(y2), n2(r), n2(r), f > 0.5 and 1 or 0)
end

-- A ring for f of a turn: an open arc, or a whole ellipse (whose fill must be cleared: a Shape's default fill
-- is white, only open arcs get a transparent one).
local function ring(cx, cy, r, f, startDeg)
	if f >= 0.999 then return fmt("Ellipse %s,%s,%s | Fill Color 00000000", n2(cx), n2(cy), n2(r)) end
	return BB.arc(cx, cy, r, max(f, 0.003), startDeg)
end

-- The progress ring: track, two glow strokes (8 % and 14 %, wider) and the stroke, its gradient running from
-- the top to the end of the arc, 55 % → 100 % (mockup ringSVG). Returns the meter's options.
function BB.ringOptions(cx, cy, d, lw, f, tint)
	local r = (d - lw) / 2
	local o = {
		Shape = fmt("Ellipse %s,%s,%s | StrokeWidth %s | Stroke Color %s | Fill Color 00000000",
			n2(cx), n2(cy), n2(r), n2(lw), BB.C.track),
		Shape2 = NONE, Shape3 = NONE, Shape4 = NONE,
	}
	if f > 0.001 then
		local caps = f >= 0.999 and "" or " | StrokeStartCap Round | StrokeEndCap Round"
		local g = ring(cx, cy, r, f)
		o.Shape2 = fmt("%s | StrokeWidth %s | Stroke Color %s%s", g, n2(lw + 5), BB.rgba(tint, 0.08), caps)
		o.Shape3 = fmt("%s | StrokeWidth %s | Stroke Color %s%s", g, n2(lw + 2.5), BB.rgba(tint, 0.14), caps)
		o.Shape4 = fmt("%s | StrokeWidth %s | Stroke LinearGradient RingGradient%s", g, n2(lw), caps)
		local e = rad(max(0.08, min(f, 0.5)) * 360)
		local angle = deg(atan2((cy - r * cos(e)) - (cy - r), (cx + r * sin(e)) - cx))
		o.RingGradient = fmt("%s | %s ; 0 | %s ; 1", n2(angle), BB.rgba(tint, 0.55), BB.rgba(tint, 1))
	end
	return o
end

-- The "connecting" arc: a 22 % arc turning around a faint track. Static (at the top) when motion is off.
function BB.spinnerOptions(cx, cy, d, lw, angle, color)
	local r = (d - lw) / 2
	return {
		Shape = fmt("Ellipse %s,%s,%s | StrokeWidth %s | Stroke Color FFFFFF14 | Fill Color 00000000",
			n2(cx), n2(cy), n2(r), n2(lw)),
		Shape2 = fmt("%s | StrokeWidth %s | Stroke Color %s | StrokeStartCap Round | StrokeEndCap Round",
			BB.arc(cx, cy, r, 0.22, angle), n2(lw), color),
	}
end

-- The progress bar (mockup .pbar): track, glow (20 %, 2 px around), fill (tint 75 % → 100 %), highlight.
-- Drawn in a meter placed 2 px up and left, so nothing has a negative coordinate.
function BB.barOptions(w, f, tint)
	local o = { Shape = fmt("Rectangle 2,2,%s,6,3 | Fill Color FFFFFF17 | StrokeWidth 0", n2(w)),
		Shape2 = NONE, Shape3 = NONE, Shape4 = NONE }
	if f > 0.001 then
		local fw = max(6, f * w)
		o.Shape2 = fmt("Rectangle 0,0,%s,10,5 | Fill Color %s | StrokeWidth 0", n2(fw + 4), BB.rgba(tint, 0.2))
		o.Shape3 = fmt("Rectangle 2,2,%s,6,3 | Fill LinearGradient BarGradient | StrokeWidth 0", n2(fw))
		o.Shape4 = fmt("Rectangle 4.4,3,%s,2.1,1 | Fill Color FFFFFF47 | StrokeWidth 0", n2(max(0, fw - 4.8)))
		o.BarGradient = fmt("0 | %s ; 0 | %s ; 1", BB.rgba(tint, 0.75), BB.rgba(tint, 1))
	end
	return o
end

-- A spool swatch of diameter d (mockup spoolSVG): track, amount-left arc (accent when active), the filament
-- colour (a 135° multi-stop gradient for multicolour spools), a gloss, a rim and the hub (BB.hubColor). Empty slot: a dashed
-- ring. Local coordinates, so the meter's rectangle is the tooltip's hit area.
function BB.spoolOptions(sp, d, active)
	local rw = max(2, d * 0.075)
	local gap = max(1.5, d * 0.06)
	local c = d / 2
	local r = c - rw / 2
	local ir = c - rw - gap
	local shapes = {}
	local o = {}
	shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | StrokeWidth %s | Stroke Color %s | Fill Color 00000000",
		n2(c), n2(c), n2(r), n2(rw), BB.C.track)
	local loaded = sp and not sp.empty
	local arcColor = active and BB.rgba(BB.C.accent, 1) or "FFFFFF8C"
	if loaded and sp.fraction then
		if sp.fraction > 0.001 then
			local caps = sp.fraction >= 0.999 and "" or " | StrokeStartCap Round | StrokeEndCap Round"
			shapes[#shapes + 1] = fmt("%s | StrokeWidth %s | Stroke Color %s%s",
				ring(c, c, r, min(1, sp.fraction)), n2(rw), arcColor, caps)
		end
	elseif active then
		shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | StrokeWidth %s | Stroke Color %s | Fill Color 00000000",
			n2(c), n2(c), n2(r), n2(rw), arcColor)
	end
	if loaded then
		local colors = sp.colors or {}
		if #colors > 1 then
			local stops = {}
			for i, col in ipairs(colors) do
				stops[#stops + 1] = fmt("%s ; %s", BB.rgba(col, 1), n2((i - 1) / (#colors - 1)))
			end
			o.SpoolColors = "45 | " .. table.concat(stops, " | ")
			shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill LinearGradient SpoolColors | StrokeWidth 0", n2(c), n2(c), n2(ir))
		else
			shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill Color %s | StrokeWidth 0", n2(c), n2(c), n2(ir),
				BB.rgba(sp.color, 1))
		end
		o.SpoolGloss = "45 | FFFFFF4D ; 0 | FFFFFF00 ; 0.5 | 00000038 ; 1"
		shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill LinearGradient SpoolGloss | StrokeWidth 0", n2(c), n2(c), n2(ir))
		shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill Color 00000000 | StrokeWidth 0.75 | Stroke Color %s",
			n2(c), n2(c), n2(ir - 0.375), BB.luminance(sp.color) > 0.85 and "FFFFFF2E" or "FFFFFF0F")
		shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill Color %s | StrokeWidth 0.5 | Stroke Color FFFFFF40",
			n2(c), n2(c), n2(d * 0.1), BB.hubColor(sp.color))
	else
		-- StrokeDashes are in stroke widths (Direct2D custom dashes); the stroke is 1, so these are px.
		shapes[#shapes + 1] = fmt("Ellipse %s,%s,%s | Fill Color 00000000 | StrokeWidth 1 | Stroke Color FFFFFF38 | StrokeDashes 2.5,2.5",
			n2(c), n2(c), n2(ir - 0.5))
	end
	for i = 1, 6 do o[i == 1 and "Shape" or ("Shape" .. i)] = shapes[i] or NONE end
	o.W = n2(d); o.H = n2(d)
	return o
end

-- The status dot (mockup .dot): 6 px with a 2 px halo at 18 %.
function BB.dotOptions(color)
	return { Shape = fmt("Ellipse 5,5,5 | Fill Color %s | StrokeWidth 0", BB.rgba(color, 0.18)),
		Shape2 = fmt("Ellipse 5,5,3 | Fill Color %s | StrokeWidth 0", BB.rgba(color, 1)) }
end

-- The finish pulse at step k (0–1): a ring growing to `grow` times its size (1.55, mockup @keyframes pulse)
-- while fading from 70 %.
function BB.pulseOptions(cx, cy, r, k, color, width, grow)
	local e = BB.easeOut(k)
	local s = 1 + ((grow or 1.55) - 1) * e
	return { Shape = fmt("Ellipse %s,%s,%s | Fill Color 00000000 | StrokeWidth %s | Stroke Color %s",
		n2(cx), n2(cy), n2(r * s), n2(width * s), BB.rgba(color, 0.7 * (1 - e))) }
end

-- The card at hover amount h (0–1).
function BB.cardOptions(w, h, hover)
	local function mix(a, b)
		return fmt("%02X%02X%02X", floor(a[1] + (b[1] - a[1]) * hover + 0.5), floor(a[2] + (b[2] - a[2]) * hover + 0.5),
			floor(a[3] + (b[3] - a[3]) * hover + 0.5))
	end
	local C = BB.CARD
	local border = C.border + (C.hoverBorder - C.border) * hover
	return {
		Shape = fmt("Rectangle 0,0,%d,%d,14 | Fill LinearGradient CardGradient | StrokeWidth 0", w, h),
		Shape2 = fmt("Rectangle 0.5,0.5,%d,%d,13.5 | Fill Color 00000000 | StrokeWidth 1 | Stroke Color FFFFFF%02X",
			w - 1, h - 1, floor(border * 255 + 0.5)),
		CardGradient = fmt("90 | %s ; 0 | %s ; 1", mix(C.top, C.hoverTop), mix(C.bottom, C.hoverBottom)),
	}
end

-- ===================================================================================== fonts

BB.faces = { text = "Segoe UI", num = "Nunito" }

-- Options for a text meter in `style`, scaled by `scale` (shrink to fit).
function BB.fontOptions(style, scale, faces, color)
	local st = BB.styles[style]
	scale = scale or 1
	faces = faces or BB.faces
	local px = st.px * scale
	local o = {
		FontFace = faces[st.face], FontSize = n2(px * 0.75), FontWeight = tostring(st.weight),
		StringCase = st.case or "None",
		FontColor = color or (st.color == "sec" and BB.C.sec or BB.C.text),
		InlineSetting = NONE, InlinePattern = NONE, InlineSetting2 = NONE, InlinePattern2 = NONE,
	}
	if st.ls then
		-- Leading | trailing, in points (MeterStringBase.cpp); no advance width, so DirectWrite keeps its own.
		o.InlineSetting = fmt("CharacterSpacing | 0 | %s", n2(st.ls * scale * 0.75))
	elseif st.pct then
		o.InlineSetting = "Size | " .. n2(st.pct.px * scale * 0.75)
		o.InlinePattern = "%"
		o.InlineSetting2 = "Color | " .. BB.C.sec
		o.InlinePattern2 = "%"
	end
	return o
end

-- ===================================================================================== layout

-- A view is the set of options for every visible meter, in card coordinates (the skin's Content container
-- sits on the card). Meters a view leaves out are hidden by the glue. `ctx.measure(fontOptions, text, w)`
-- returns the text's width and height (w = wrap width, or nil for one line).
local View = {}
View.__index = View

function BB.newView(ctx, size)
	return setmetatable({ out = {}, ctx = ctx, size = size, lineCache = ctx.lineCache or {} }, View)
end

function View:set(name, o)
	o.Hidden = "0"
	self.out[name] = o
	return o
end

function View:measure(fo, text, w)
	return self.ctx.measure(fo, text, w)
end

function View:lineHeight(fo)
	local k = fo.FontFace .. "/" .. fo.FontSize .. "/" .. fo.FontWeight
	local h = self.lineCache[k]
	if not h then
		local _
		_, h = self:measure(fo, "Ag", nil)
		self.lineCache[k] = h
	end
	return h
end

-- Places a text meter. opt: w (box width: ellipsis, or wrap when lines > 1), lines, align ("Left",
-- "Right", "Center" or "CenterCenter"…), color, fit (shrink to fit down to this scale), fitWords (shrink
-- until the longest word fits), measureOnly. Returns the height the text takes (CSS line boxes) and its
-- natural width.
function View:text(name, style, text, x, y, opt)
	opt = opt or {}
	local st = BB.styles[style]
	local faces = self.ctx.faces
	local scale = 1
	local fo = BB.fontOptions(style, 1, faces, opt.color)
	local natural = self:measure(fo, text, nil)
	if opt.fit and opt.w then
		local need = natural
		if opt.fitWords then
			need = 0
			for word in string.gmatch(text, "%S+") do need = max(need, (self:measure(fo, word, nil))) end
		end
		if need > opt.w then
			scale = max(opt.fit, floor(opt.w / need * 20) / 20)
			fo = BB.fontOptions(style, scale, faces, opt.color)
			natural = self:measure(fo, text, nil)
		end
	end
	local L = self:lineHeight(fo)
	local lh = st.px * scale * st.lh
	local lines = 1
	if opt.lines and opt.lines > 1 and opt.w and natural > opt.w then
		local _, h = self:measure(fo, text, opt.w)
		lines = max(1, min(opt.lines, floor(h / L + 0.5)))
	end
	local used = (lines - 1) * L + lh
	if opt.measureOnly then return used, natural, lines end
	fo.Text = BB.esc(text)
	fo.X = n2(x)
	fo.Y = n2(y - (L - lh) / 2)
	fo.StringAlign = opt.align or "Left"
	if opt.w then
		fo.W = n2(opt.w)
		-- Wrapping text may use up to opt.lines lines; the layout above used the measured count.
		local maxLines = opt.lines or 1
		fo.ClipString = maxLines > 1 and "2" or "1"
		fo.ClipStringH = maxLines > 1 and n2(L * maxLines + 1) or NONE
	else
		fo.W = NONE
		fo.ClipString = "0"
		fo.ClipStringH = NONE
	end
	fo.ToolTipText = opt.tooltip and BB.esc(opt.tooltip) or NONE
	self:set(name, fo)
	return used, natural, lines
end

function View:icon(name, glyph, x, y, size, color, alpha)
	local file = BB.ICONS[glyph] or glyph
	local hex = "#" .. sub(BB.rgba(color, 1), 1, 6)
	local o = {
		SvgImage = (self.ctx.iconDir or "") .. file .. ".svg",
		X = n2(x), Y = n2(y), W = n2(size), H = n2(size),
		Attribute = "svg | fill | " .. hex,
		Attribute2 = "svg | fill-opacity | " .. n2(alpha or 1),
		Attribute3 = BB.STROKE_ICONS[file] and ("s | stroke | " .. hex) or NONE,
		Attribute4 = BB.STROKE_ICONS[file] and ("s | stroke-opacity | " .. n2(alpha or 1)) or NONE,
	}
	return self:set(name, o)
end

-- Secondary text colour as (hex, alpha) for icons.
local SEC_HEX, SEC_A = "EBEBF5", 0.62

function View:shape(name, x, y, o)
	o.X = n2(x); o.Y = n2(y)
	return self:set(name, o)
end

function View:art(m, x, y, w, h)
	-- The settled art is ArtA; ArtB only appears during a crossfade (set by the glue).
	local dir = self.ctx.artDir or ""
	local o = { X = n2(x), Y = n2(y), W = n2(w), H = n2(h),
		ImageName = m.art and BB.esc(dir .. m.art) or NONE,
		Greyscale = m.stale and "1" or "0", ImageAlpha = m.stale and "140" or "255" }
	self:set("ArtA", o)
	self.artBox = { x = x, y = y, w = w, h = h }
end

-- ------------------------------------------------------------------------------------- progress parts

-- The meters that follow the progress value `p` (tweened by the glue), for the current layout.
function BB.progressParts(m, size, p, geo)
	local out = {}
	if not geo then return out end
	if geo.ring then
		local g = geo.ring
		local o = BB.ringOptions(g.cx, g.cy, g.d, g.lw, p, g.tint)
		o.X, o.Y = "0", "0"
		out.Ring = o
	end
	if geo.dialPct then
		out.DialPct = { Text = tostring(floor(p * 100 + 1e-9)) .. "%" }
	end
	if geo.bar then
		local o = BB.barOptions(geo.bar.w, p, geo.bar.tint)
		out.Bar = o
	end
	if geo.pct then
		out.Pct = { Text = tostring(floor(p * 100 + 1e-9)) .. "%" }
	end
	return out
end

-- ------------------------------------------------------------------------------------- Small

local function dialGlyph(m)
	if m.state == "preparing" then return "nozzle", 19, BB.C.nozzle end
	if m.state == "finished" then return "check", 22, m.stateColor end
	if m.state == "failed" then
		if m.canceled then return "cross", 19, m.tint end
		return "bang", 19, m.tint
	end
end

function BB.viewSmall(m, ctx)
	local v = BB.newView(ctx, "Small")
	local P, colX, colW, fullW = 16, 88, 76, 148
	local geo = {}
	local cx, cy = 47, 47

	-- Dial
	local ringed = m.live and m.state ~= "idle"
	if ringed then
		local tint = (m.state == "preparing" or m.state == "finished") and m.stateColor or m.tint
		geo.ring = { cx = cx, cy = cy, d = 62, lw = 7, tint = tint }
		v:set("Ring", {})
		if m.state == "printing" or m.state == "paused" then
			geo.dialPct = true
			local up = m.state == "paused" and 5 or 0
			v:text("DialPct", "dialPct", m.percent .. "%", cx, cy - up - 18 * 1.1 / 2, { align = "Center" })
			if m.state == "paused" then v:icon("DialIcon", "pause", cx - 4.5, cy + 6, 9, BB.C.paused) end
		else
			local glyph, s, col = dialGlyph(m)
			v:icon("DialIcon", glyph, cx - s / 2, cy - s / 2, s, col)
		end
	else
		v:art(m, P + 2, P + 2, 58, 58)
		if m.link == "connecting" then
			geo.spinner = { cx = cx, cy = cy, d = 62, lw = 3, color = "EBEBF58C" }
			v:set("Spinner", {})
		end
	end

	-- Column beside the dial
	local y = P
	local reflow = false
	if m.live and (m.state == "paused" or m.state == "preparing") and m.reason and m.reason ~= "" then
		local fo = BB.fontOptions("reason", 1, ctx.faces)
		for word in string.gmatch(m.reason, "%S+") do
			if v:measure(fo, word, nil) > colW then reflow = true end
		end
	end
	geo.reflow = reflow

	if m.live then
		y = y + v:text("Caption", "cap", m.caption or "", colX, y, { w = colW, fit = 0.8, color = m.captionColor }) + 1
		if m.state == "printing" then
			y = y + v:text("Big", "big", m.countdown or "", colX, y, { w = colW, fit = 0.7 }) + 1
			if m.leftWord and m.leftWord ~= "" then y = y + v:text("Sub", "sub", m.leftWord, colX, y, { w = colW }) + 1 end
		elseif m.state == "preparing" or m.state == "paused" then
			if reflow then
				if m.state == "paused" and m.layers then
					y = y + v:text("Sub", "subNum", m.detailFirst or "", colX, y, { w = colW }) + 1
				end
			else
				y = y + v:text("Reason", "reason", m.reason or "", colX, y, { w = colW, lines = 3 }) + 1
			end
		elseif m.state == "finished" or m.state == "failed" then
			local job = m.lastJob or {}
			y = y + v:text("Big", "bigAgo", job.ago or "", colX, y, { w = colW, fit = 0.7 }) + 1
			if job.took then y = y + v:text("Sub", "sub", job.took, colX, y, { w = colW }) + 1 end
		elseif m.state == "idle" then
			y = y + 2
			for i = 1, min(3, #m.temps) do
				local t = m.temps[i]
				local tv, tt = BB.num(t.value), BB.num(t.target)
				local hot = tt and tv and tv < tt
				local used = v:text("Temp" .. i, "tempRow", t.value or "", colX + 14, y, {})
				v:icon("TempIcon" .. i, t.kind, colX, y + (used - 10) / 2, 10, hot and t.color or SEC_HEX, hot and 1 or SEC_A)
				y = y + used + 2
			end
		end
	else
		y = y + v:text("Caption", "capTwo", m.headline or m.caption or "", colX, y,
			{ w = colW, lines = 2, fit = 0.8, fitWords = true, color = m.captionColor }) + 1
		if m.detail and m.detail ~= "" then
			y = y + v:text("Reason", "noLive", m.detail, colX, y + 1, { w = colW, lines = 3 }) + 2
		end
	end
	local topBottom = max(P + 62, y)

	-- Footer, from the bottom up
	local bottom = 180 - P
	local footTop = bottom
	if m.live and m.state == "idle" then
		local hd = v:text("FootDetail", "footLine", m.stateLine or "", P, 0, { w = fullW, measureOnly = true })
		footTop = bottom - hd - 4 - 21
		v:text("FootDetail", "footLine", m.stateLine or "", P, bottom - hd, { w = fullW })
		local x, n = P, 0
		for _, sp in ipairs(m.spools) do
			if not sp.empty and n < 5 then
				n = n + 1
				local o = BB.spoolOptions(sp, 21, false)
				o.ToolTipText = BB.esc(sp.tooltip or "")
				v:shape("Swatch" .. n, x, footTop, o)
				x = x + 26
			end
		end
	elseif m.live then
		local detail = (not reflow) and m.detail or nil
		local hd = 0
		if detail and detail ~= "" then
			hd = v:text("FootDetail", "footDetail", detail, P, 0, { w = fullW, measureOnly = true })
			v:text("FootDetail", "footDetail", detail, P, bottom - hd, { w = fullW })
		end
		local ht = v:text("FootTitle", "footTitle", m.headline or "", P, 0, { w = fullW, measureOnly = true })
		footTop = bottom - hd - ht
		v:text("FootTitle", "footTitle", m.headline or "", P, footTop, { w = fullW })
	else
		local hd = v:text("FootDetail", "footLine", m.stateLine or "", P, 0, { w = fullW, lines = 2, measureOnly = true })
		v:text("FootDetail", "footLine", m.stateLine or "", P, bottom - hd, { w = fullW, lines = 2 })
		local ht = v:text("FootTitle", "footTitle", m.name or "", P, 0, { w = fullW, measureOnly = true })
		footTop = bottom - hd - ht
		v:text("FootTitle", "footTitle", m.name or "", P, footTop, { w = fullW })
	end

	-- Middle: the reflowed reason under the dial, or the active spool
	if reflow then
		v:text("ReasonFull", "reason", m.reason, P, topBottom + 8, { w = fullW, lines = 3 })
	elseif m.active and m.activeSpool and m.activeSpoolLine then
		local ys = topBottom + max(0, (footTop - topBottom - 16) / 2)
		local o = BB.spoolOptions(m.activeSpool, 16, true)
		o.ToolTipText = BB.esc(m.activeSpool.tooltip or "")
		v:shape("ActiveSpool", P, ys, o)
		local used = v:text("ActiveSpoolText", "spoolLine", m.activeSpoolLine, P + 21, 0, { w = fullW - 21, measureOnly = true })
		v:text("ActiveSpoolText", "spoolLine", m.activeSpoolLine, P + 21, ys + (16 - used) / 2, { w = fullW - 21 })
	end

	v.geo = geo
	geo.pulse = ringed and { cx = cx, cy = cy, r = 29.5, width = 3, color = BB.C.finished } or nil
	return v
end

-- ------------------------------------------------------------------------------------- Medium / Large

-- The top row, shared by Medium (180 high) and Large (its first 125 px). Returns the column's bottom.
local function mediumInner(v, m, top, rowH, inLarge)
	local P = 16
	local colX, colW, right = 128, 236, 364
	local geo = v.geo
	v:art(m, P, top + (rowH - 116) / 2, 98, 116)
	local bottom = top + rowH

	-- Header: printer name, then the state (dot + caption), a spinner or a key.
	local y = top
	local hh = 15
	local rightW = 0
	if m.live then
		local used, w = v:text("Status", "status", m.caption or "", right, 0, { measureOnly = true })
		v:text("Status", "status", m.caption or "", right, y + (hh - used) / 2, { align = "Right", color = m.captionColor })
		local dotCx = right - w - 5 - 3 -- 6 px dot, 5 px before the caption
		v:shape("Dot", dotCx - 5, y + hh / 2 - 5, BB.dotOptions(m.stateColor))
		geo.dot = { cx = dotCx, cy = y + hh / 2, color = m.stateColor }
		rightW = w + 5 + 6 + 8
	elseif m.link == "connecting" then
		geo.spinner = { cx = right - 6, cy = y + hh / 2, d = 12, lw = 2, color = "EBEBF5B3" }
		v:set("Spinner", {})
		rightW = 12 + 8
	elseif m.link == "unauthorized" then
		v:icon("HeaderIcon", "key", right - 13, y + 1, 13, BB.C.paused)
		rightW = 13 + 8
	end
	local nameUsed = v:text("Name", "name", m.name or "", colX, 0, { measureOnly = true })
	v:text("Name", "name", m.name or "", colX, y + (hh - nameUsed) / 2, { w = colW - rightW })
	y = y + hh + 3
	y = y + v:text("Headline", "headline", m.headline or "", colX, y, { w = colW })

	if m.live and m.active then
		local pctUsed = v:text("Pct", "pct", m.percent .. "%", colX, y, {})
		geo.pct = true
		if m.layers then
			local lyUsed = v:text("Layers", "layers", m.layers, right, 0, { measureOnly = true })
			v:text("Layers", "layers", m.layers, right, y + pctUsed - lyUsed - 3, { align = "Right" })
		end
		y = y + pctUsed + 5
		geo.bar = { w = colW, tint = m.tint }
		v:shape("Bar", colX - 2, y - 2, {})
		y = y + 6 + 5
		local rw = 0
		if m.finish then
			local _, w = v:text("Finish", "slineRight", m.finish, right, y, { align = "Right" })
			rw = w + 8
		end
		v:text("StateLine", "sline", m.stateLine or "", colX, y, { w = colW - rw })
		if not inLarge then
			local tx = colX
			for i = 1, min(3, #m.temps) do
				local t = m.temps[i]
				local ty = bottom - 11.5 * 1.25
				v:icon("TempIcon" .. i, t.kind, tx, ty + 2.2, 10, t.target and t.color or SEC_HEX, t.target and 1 or SEC_A)
				local _, w = v:text("Temp" .. i, "tempInline", t.value or "", tx + 13, ty, {})
				tx = tx + 13 + w + 12
			end
		end
	else
		y = y + 2
		v:text("Line2", "line2", m.stateLine or "", colX, y, { w = colW, lines = 2 })
		if m.live then
			local x = colX
			for i = 1, min(8, #m.spools) do
				local sp = m.spools[i]
				local o = BB.spoolOptions(sp, 23, false)
				o.ToolTipText = BB.esc(sp.tooltip or "")
				v:shape("Swatch" .. i, x, bottom - 23, o)
				x = x + 29
			end
		end
	end
	return bottom
end

function BB.viewMedium(m, ctx)
	local v = BB.newView(ctx, "Medium")
	v.geo = {}
	mediumInner(v, m, 16, 148, false)
	-- No ring in this size: the finish pulse leaves the status dot instead, and travels further.
	v.geo.pulse = v.geo.dot and { cx = v.geo.dot.cx, cy = v.geo.dot.cy, r = 3, width = 2, color = BB.C.finished, grow = 4 } or nil
	return v
end

function BB.viewLarge(m, ctx)
	local v = BB.newView(ctx, "Large")
	v.geo = {}
	local P, W = 16, 348
	local rowBottom = mediumInner(v, m, P, 125, true)
	local bottom = 380 - P
	if m.live then
		-- Temperature tiles
		local y = rowBottom + 14
		local n = min(3, #m.temps)
		local tileW = (W - 8 * 2) / 3
		local tileH = 7 + 11 * 1.25 + 2 + 19 * 1.15 + 7
		for i = 1, n do
			local t = m.temps[i]
			local x = P + (i - 1) * (tileW + 8)
			local tip = (t.label or "") .. ": " .. (t.value or "") .. (t.target and (" → " .. t.target) or "")
			v:shape("Tile" .. i, x, y, { Shape = fmt("Rectangle 0,0,%s,%s,11 | Fill Color %s | StrokeWidth 0",
				n2(tileW), n2(tileH), BB.C.tile), ToolTipText = BB.esc(tip) })
			v:icon("TileIcon" .. i, t.kind, x + 9, y + 7 + 2, 10, t.target and t.color or SEC_HEX, t.target and 1 or SEC_A)
			v:text("TileLabel" .. i, "tileLabel", t.label or "", x + 9 + 13, y + 7, { w = tileW - 18 - 13 })
			local vy = y + 7 + 11 * 1.25 + 2
			local _, w = v:text("TileValue" .. i, "tileValue", t.value or "", x + 9, vy, {})
			local tv, tt = BB.num(t.value), BB.num(t.target)
			if tv and tt and abs(tv - tt) > 2 then
				local tu = v:text("TileTarget" .. i, "tileTarget", "→ " .. t.target, x + 9 + w + 4, 0, { measureOnly = true })
				v:text("TileTarget" .. i, "tileTarget", "→ " .. t.target, x + 9 + w + 4, vy + 19 * 1.15 - tu - 2,
					{ color = BB.rgba(t.color, 1) })
			end
		end
		y = y + tileH + 14

		-- The first AMS unit
		if m.ams and #m.spools > 0 then
			local hu = v:text("AmsTitle", "amsTitle", m.ams.title or "", P, y, { w = W - 60 })
			if m.ams.humidity then
				local _, w = v:text("AmsHum", "amsHum", m.ams.humidity, P + W, y, { align = "Right" })
				v:icon("AmsHumIcon", "drop", P + W - w - 13, y + (hu - 10) / 2, 10, SEC_HEX, SEC_A)
			end
			y = y + hu + 9
			local n = min(4, #m.spools)
			local colW = W / max(n, 1)
			for i = 1, n do
				local sp = m.spools[i]
				local x = P + (i - 1) * colW
				local o = BB.spoolOptions(sp, 36, m.active and sp.active)
				o.ToolTipText = BB.esc(sp.tooltip or "")
				v:shape("Spool" .. i, x + (colW - 36) / 2, y, o)
				local my = y + 36 + 3
				local mu = v:text("SpoolMat" .. i, "spoolMat", sp.name or "", x + colW / 2, my,
					{ align = "Center", w = colW - 4 })
				v:text("SpoolAmt" .. i, "spoolAmt", sp.amount or "", x + colW / 2, my + mu + 3,
					{ align = "Center", w = colW - 4 })
			end
		end

		-- Footer: the most severe issue, else the last job
		if m.issue and m.issue.text then
			local tw = W - 13 - 6
			local used = v:text("FootText", "foot", m.issue.text, P + 19, 0, { w = tw, lines = 2, measureOnly = true })
			local fy = bottom - used
			v:text("FootText", "foot", m.issue.text, P + 19, fy, { w = tw, lines = 2 })
			v:icon("FootIcon", "warn", P, fy + 1, 13, BB.C.paused)
		elseif m.lastJob and m.lastJob.name then
			local job = m.lastJob
			local rw = 0
			local fy = bottom - 11.5 * 1.25
			if job.ago then
				local _, w = v:text("FootRight", "footRight", job.ago, P + W, fy, { align = "Right" })
				rw = w + 8
			end
			v:text("FootText", "foot", job.line or job.name, P + 19, fy, { w = W - 19 - rw })
			local ok = job.outcome == "finished"
			v:icon("FootIcon", ok and "okCircle" or "xCircle", P, fy + 0.5, 13, ok and BB.C.finished or BB.C.failed)
		end
	else
		-- Not live: an info box that says what to do.
		local text = m.info or m.stateLine or ""
		local tx, tw = P + 12 + 14 + 8, W - 24 - 22
		local used = v:text("InfoText", "info", text, tx, 0, { w = tw, lines = 4, measureOnly = true })
		local boxH = 24 + used
		local by = bottom - boxH
		v:shape("InfoBox", P, by, { Shape = fmt("Rectangle 0,0,%s,%s,12 | Fill Color %s | StrokeWidth 0",
			n2(W), n2(boxH), BB.C.tile) })
		local glyph = m.link == "unconfigured" and "printer" or (m.link == "unauthorized" and "key" or "wifiOff")
		local unauth = m.link == "unauthorized"
		v:icon("InfoIcon", glyph, P + 12, by + 13, 14, unauth and BB.C.paused or SEC_HEX, unauth and 1 or SEC_A)
		v:text("InfoText", "info", text, tx, by + 12, { w = tw, lines = 4 })
	end
	v.geo.pulse = v.geo.dot and { cx = v.geo.dot.cx, cy = v.geo.dot.cy, r = 3, width = 2, color = BB.C.finished, grow = 4 } or nil
	return v
end

BB.views = { Small = BB.viewSmall, Medium = BB.viewMedium, Large = BB.viewLarge }

-- Every meter a size's view may set (the glue hides the ones a view leaves out).
BB.METERS = {
	Small = { "ArtA", "ArtB", "Ring", "Spinner", "DialPct", "DialIcon", "Caption", "Big", "Sub", "Reason",
		"TempIcon1", "Temp1", "TempIcon2", "Temp2", "TempIcon3", "Temp3", "ReasonFull", "ActiveSpool",
		"ActiveSpoolText", "Swatch1", "Swatch2", "Swatch3", "Swatch4", "Swatch5", "FootTitle", "FootDetail" },
	Medium = { "ArtA", "ArtB", "Name", "Dot", "Status", "Spinner", "HeaderIcon", "Headline", "Pct", "Layers", "Bar",
		"StateLine", "Finish", "TempIcon1", "Temp1", "TempIcon2", "Temp2", "TempIcon3", "Temp3", "Line2",
		"Swatch1", "Swatch2", "Swatch3", "Swatch4", "Swatch5", "Swatch6", "Swatch7", "Swatch8" },
}
BB.METERS.Large = {}
for _, n in ipairs(BB.METERS.Medium) do
	if not find(n, "^Temp") then BB.METERS.Large[#BB.METERS.Large + 1] = n end
end
for _, n in ipairs({ "Tile1", "TileIcon1", "TileLabel1", "TileValue1", "TileTarget1", "Tile2", "TileIcon2",
	"TileLabel2", "TileValue2", "TileTarget2", "Tile3", "TileIcon3", "TileLabel3", "TileValue3", "TileTarget3",
	"AmsTitle", "AmsHumIcon", "AmsHum", "Spool1", "SpoolMat1", "SpoolAmt1", "Spool2", "SpoolMat2", "SpoolAmt2",
	"Spool3", "SpoolMat3", "SpoolAmt3", "Spool4", "SpoolMat4", "SpoolAmt4", "FootIcon", "FootText", "FootRight",
	"InfoBox", "InfoIcon", "InfoText" }) do
	BB.METERS.Large[#BB.METERS.Large + 1] = n
end

-- The complete view for a model: layout + the progress meters at value p + the spinner at `angle`.
function BB.render(m, size, ctx, p, angle)
	local v = BB.views[size](m, ctx)
	local parts = BB.progressParts(m, size, p or BB.dialValue(m, size), v.geo)
	for name, o in pairs(parts) do
		local t = v.out[name] or {}
		for k, x in pairs(o) do t[k] = x end
		t.Hidden = "0"
		v.out[name] = t
	end
	if v.geo.spinner then
		local g = v.geo.spinner
		local o = BB.spinnerOptions(g.cx, g.cy, g.d, g.lw, angle or 0, g.color)
		o.X, o.Y, o.Hidden = "0", "0", "0"
		v.out.Spinner = o
	end
	for _, name in ipairs(BB.METERS[size]) do
		if not v.out[name] then v.out[name] = { Hidden = "1" } end
	end
	return v
end

-- ===================================================================================== Rainmeter glue

local G = {
	applied = {}, -- meter → option → value last sent
	touched = {}, -- meters changed since the last redraw
	anims = {}, -- name → tween
	frameRunning = false, spinRunning = false,
	hover = 0,
}
BB.G = G

-- Clocks: os.clock is wall time on Windows (the MSVC CRT clock()), used for animation; os.time for the
-- state file's unix times. Tests replace both.
local function clock() return (BB.clock or os.clock)() end
local function now() return (BB.now or os.time)() end

-- Sends only the options that changed.
local function apply(name, o)
	local a = G.applied[name]
	if not a then a = {}; G.applied[name] = a end
	local changed = false
	for k, v in pairs(o) do
		if a[k] ~= v then
			SKIN:SetOption(name, k, v)
			a[k] = v
			changed = true
		end
	end
	if changed then G.touched[name] = true end
end

local function flush()
	for name in pairs(G.touched) do SKIN:Bang("!UpdateMeter", name) end
	G.touched = {}
	SKIN:Bang("!Redraw")
end

-- Text measurement with the two invisible probe meters (see the note at the top). Results are cached: the
-- layout runs every second, but its texts rarely change.
local function measure(fo, text, w)
	local key = table.concat({ fo.FontFace, fo.FontSize, fo.FontWeight, fo.StringCase or "", fo.InlineSetting or "",
		tostring(w), text }, "\1")
	local hit = G.measured[key]
	if hit then return hit[1], hit[2] end
	if G.measuredCount > 2000 then G.measured, G.measuredCount = {}, 0 end
	local probe = w and "ProbeWrap" or "ProbeLine"
	local o = {}
	for k, v in pairs(fo) do
		if k == "FontFace" or k == "FontSize" or k == "FontWeight" or k == "StringCase" or k == "InlineSetting"
			or k == "InlinePattern" or k == "InlineSetting2" or k == "InlinePattern2" then o[k] = v end
	end
	o.Text = BB.esc(text ~= "" and text or " ")
	if w then o.W = n2(w) end
	apply(probe, o)
	SKIN:Bang("!UpdateMeter", probe)
	G.touched[probe] = nil
	local meter = SKIN:GetMeter(probe)
	local mw, mh = meter:GetW(), meter:GetH()
	G.measured[key] = { mw, mh }
	G.measuredCount = G.measuredCount + 1
	return mw, mh
end

local function ctx()
	return { measure = measure, faces = BB.faces, iconDir = G.iconDir, artDir = G.artDir, lineCache = G.lineCache }
end

-- ActionTimer expands "Repeat" into a list of commands (MeasureActionTimer.cpp), so the skins use short lists
-- (ActionList1: 150 frames of 16 ms, ActionList2: 200 steps of 50 ms) and Lua restarts one that would run out
-- while something still moves ("Stop N" resets a list at once).
BB.FRAME_LIST, BB.SPIN_LIST = 150, 200

local function startFrames()
	if not G.frameRunning then
		G.frameRunning = true
		G.frames = 0
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Execute 1")
	end
end

local function animate(name, from, to, ms, ease, onDone)
	G.anims[name] = { from = from, to = to, t0 = clock(), ms = ms, ease = ease, onDone = onDone }
	startFrames()
end

local function setContentAlpha(a)
	apply("Content", { Shape = fmt("Rectangle 0,0,%d,%d,14 | Fill Color FFFFFF%02X | StrokeWidth 0",
		G.w, G.h, floor(max(0, min(1, a)) * 255 + 0.5)) })
end

local function applyProgress(p)
	G.dispP = p
	if not G.model or not G.view then return end
	for name, o in pairs(BB.progressParts(G.model, G.size, p, G.view.geo)) do apply(name, o) end
end

local function spinAngle()
	if not G.model or not G.model.motion then return 0 end
	return (clock() * 1000 / BB.T.spinTurn * 360) % 360
end

-- Lays out the model and sends what changed. p = progress to draw (a tween may be running).
local function show(m, p)
	G.model = m
	G.view = BB.render(m, G.size, ctx(), p or G.dispP or BB.dialValue(m, G.size), spinAngle())
	G.dispP = p or BB.dialValue(m, G.size)
	-- Art: during a crossfade ArtA keeps the old picture and ArtB fades the new one in over it.
	if G.anims.art and G.artHold and G.view.out.ArtA.ImageName then
		G.view.out.ArtA.ImageName = BB.esc((G.artDir or "") .. G.artHold)
	end
	for name, o in pairs(G.view.out) do apply(name, o) end
	if not G.anims.art then apply("ArtB", { Hidden = "1" }) end
	apply("Card", { LeftMouseUpAction = "[\"" .. m.url .. "\"]" })
end

local function stopSpinner()
	if G.spinRunning then
		G.spinRunning = false
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Stop 2")
	end
end

local function updateSpinner()
	local want = G.view and G.view.geo.spinner and G.model.motion
	if want then
		if not G.spinRunning then
			G.spinRunning = true
			G.spins = 0
			SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Execute 2")
		end
	else
		stopSpinner()
	end
end

local function crossfadeArt(tr, m)
	local box = G.view and G.view.artBox
	if not box or not tr.to then return end
	local dir = G.artDir or ""
	local alpha = m.stale and 140 or 255
	apply("ArtB", { X = n2(box.x), Y = n2(box.y), W = n2(box.w), H = n2(box.h), ImageName = BB.esc(dir .. tr.to),
		Greyscale = m.stale and "1" or "0", ImageAlpha = "0", Hidden = "0" })
	-- ArtA keeps the old picture under the new one until the fade ends.
	G.artHold = tr.from
	if tr.from then apply("ArtA", { ImageName = BB.esc(dir .. tr.from) }) end
	animate("art", 0, alpha, tr.ms, BB.smooth, function()
		G.artHold = nil
		apply("ArtA", { ImageName = BB.esc(dir .. tr.to), ImageAlpha = tostring(alpha) })
		apply("ArtB", { Hidden = "1" })
	end)
end

local function startPulse()
	local g = G.view and G.view.geo.pulse
	if not g then return end
	animate("pulse", 0, 1, BB.T.pulse, function(t) return t end, function() apply("Pulse", { Hidden = "1" }) end)
end

-- A new model: decide what moves.
local function present(m)
	local prev = G.model
	local c = BB.changes(prev, m, G.size)
	if c.first or not m.motion then
		G.anims = {}
		show(m, BB.dialValue(m, G.size))
		apply("ArtB", { Hidden = "1" }); apply("Pulse", { Hidden = "1" })
		setContentAlpha(1)
		updateSpinner()
		flush()
		return
	end
	if c.state then
		-- Fade the content out, swap, fade in; the ring and bar tween after the swap.
		G.pending = m
		G.pendingChanges = c
		if not G.anims.fadeOut then
			local from = G.anims.fadeIn and BB.tweenAt(G.anims.fadeIn, clock()) or 1
			G.anims.fadeIn = nil
			animate("fadeOut", from, 0, BB.T.fadeOut * from, BB.smooth, function()
				local pm, pc = G.pending, G.pendingChanges
				G.pending, G.pendingChanges = nil, nil
				local fromP = G.dispP or 0
				-- A crossfade still running belongs to the old content: drop it.
				G.anims.art, G.artHold = nil, nil
				apply("ArtB", { Hidden = "1" })
				show(pm, fromP)
				if pc.art and pc.art.to and pc.art.from then
					-- The content is invisible now; the new art just appears with it.
					apply("ArtA", { ImageName = BB.esc((G.artDir or "") .. pc.art.to) })
				end
				updateSpinner()
				animate("fadeIn", 0, 1, BB.T.fadeIn, BB.smooth)
				local toP = BB.dialValue(pm, G.size)
				local finished = pc.finished
				if fromP ~= toP then
					animate("progress", fromP, toP, BB.T.tween, BB.easeOut, finished and startPulse or nil)
				elseif finished then
					startPulse()
				end
			end)
		end
		return
	end
	if G.pending then G.pending = m; return end -- a swap is under way; it will show the latest
	local p = G.dispP
	local running = G.anims.progress
	if c.progress and running then
		-- Retarget from where the ring is now.
		running.from, running.to, running.t0 = BB.tweenAt(running, clock()), c.progress.to, clock()
	elseif c.progress then
		animate("progress", G.dispP or c.progress.from, c.progress.to, BB.T.tween, BB.easeOut)
	elseif not running then
		p = BB.dialValue(m, G.size)
	end
	show(m, p)
	if c.art then crossfadeArt(c.art, m) end
	updateSpinner()
	flush()
end

-- ------------------------------------------------------------------------------------- entry points

function Initialize()
	local res = SKIN:ReplaceVariables("#@#")
	BB.json = BB.json or dofile(res .. "Scripts\\json.lua")
	G.size = SELF:GetOption("Size", "Small")
	local dims = BB.SIZES[G.size] or BB.SIZES.Small
	G.w, G.h = dims.w, dims.h
	G.iconDir = res .. "Icons\\"
	-- Resolved (the default refers to #@#), not GetVariable's raw value.
	G.stateFile = SKIN:ReplaceVariables("#StateFile#")
	G.artDir = string.match(G.stateFile, "^(.*[\\/])") or ""
	G.lineCache, G.measured, G.measuredCount = {}, {}, 0
	G.applied, G.touched, G.anims, G.model, G.view, G.raw, G.state = {}, {}, {}, nil, nil, nil, nil
	G.pending, G.artHold, G.hover, G.frameRunning, G.spinRunning = nil, nil, 0, false, false
	-- Segoe UI Variable ships with Windows 11 only; Windows 10 has Segoe UI (mockup O7).
	local windir = os.getenv("WINDIR") or "C:\\Windows"
	local f = io.open(windir .. "\\Fonts\\SegUIVar.ttf", "rb")
	if f then f:close(); BB.faces.text = "Segoe UI Variable Text" else BB.faces.text = "Segoe UI" end
	apply("Card", BB.cardOptions(G.w, G.h, 0))
	setContentAlpha(1)
end

function Update()
	local text = SKIN:ReadTextFile(G.stateFile)
	if text and text ~= G.raw then
		local s = BB.json.decode(text)
		if type(s) == "table" then
			G.raw = text
			G.state = BB.clean(s, BB.json.null)
		end
	end
	local s = G.state or { v = 1, link = "unconfigured", live = false, state = "idle", caption = "Bambu Bar",
		headline = "Open Bambu Bar", stateLine = "", motion = true }
	present(BB.model(s, now()))
	return G.model and G.model.percent or 0
end

-- One animation frame (ActionList1, every ~16 ms while something moves).
function Frame()
	local now = clock()
	local done = {}
	for name, tw in pairs(G.anims) do
		local v, finished = BB.tweenAt(tw, now)
		if name == "fadeOut" or name == "fadeIn" then
			setContentAlpha(v)
		elseif name == "progress" then
			applyProgress(v)
		elseif name == "art" then
			apply("ArtB", { ImageAlpha = tostring(floor(v + 0.5)) })
		elseif name == "hover" then
			G.hover = v
			apply("Card", BB.cardOptions(G.w, G.h, v))
		elseif name == "pulse" then
			local g = G.view and G.view.geo.pulse
			if g then
				local o = BB.pulseOptions(BB.MARGIN + g.cx, BB.MARGIN + g.cy, g.r, v, g.color, g.width, g.grow)
				o.Hidden = "0"
				apply("Pulse", o)
			end
		end
		if finished then done[#done + 1] = name end
	end
	for _, name in ipairs(done) do
		local tw = G.anims[name]
		G.anims[name] = nil
		if tw and tw.onDone then tw.onDone() end
	end
	G.frames = (G.frames or 0) + 1
	if next(G.anims) == nil then
		if G.frameRunning then
			G.frameRunning = false
			SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Stop 1")
		end
	elseif G.frames >= BB.FRAME_LIST - 10 then
		G.frames = 0
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Stop 1")
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Execute 1")
	end
	flush()
end

-- The connecting arc (ActionList2, every 50 ms ≈ 20 fps, only while connecting).
function Spin()
	local g = G.view and G.view.geo.spinner
	if not g or not G.model or not G.model.motion then stopSpinner(); return end
	apply("Spinner", BB.spinnerOptions(g.cx, g.cy, g.d, g.lw, spinAngle(), g.color))
	G.spins = (G.spins or 0) + 1
	if G.spins >= BB.SPIN_LIST - 10 then
		G.spins = 0
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Stop 2")
		SKIN:Bang("!CommandMeasure", "MeasureAnimation", "Execute 2")
	end
	flush()
end

-- MouseOverAction / MouseLeaveAction of the card.
function Hover(on)
	local to = on == 1 and 1 or 0
	if G.model and not G.model.motion then
		G.hover = to
		apply("Card", BB.cardOptions(G.w, G.h, to))
		flush()
		return
	end
	animate("hover", G.hover, to, BB.T.hover * abs(to - G.hover), BB.smooth)
end
