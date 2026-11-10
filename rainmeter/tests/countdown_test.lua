local T = require("lib")
local BB = T.BB

local EN = T.state("printing").strings
-- The app's German patterns (shared/Localizable.xcstrings, as WidgetState.cs writes them).
local DE = { lessThanMinute = "<1 min", minutes = "{0} min", hoursMinutes = "{0} h {1} min", daysHours = "{0} d {1} h",
	left = "Noch {0}", done = "Fertig {0}" }

T.test("English countdown, as the Mac's Format.duration", function()
	T.eq(BB.duration(0, EN), "<1m")
	T.eq(BB.duration(-3, EN), "<1m")
	T.eq(BB.duration(1, EN), "1m")
	T.eq(BB.duration(59, EN), "59m")
	T.eq(BB.duration(60, EN), "1h 00m")
	T.eq(BB.duration(62, EN), "1h 02m")
	T.eq(BB.duration(23 * 60 + 59, EN), "23h 59m")
	T.eq(BB.duration(24 * 60, EN), "1d 0h")
	T.eq(BB.duration(50 * 60 + 30, EN), "2d 2h")
end)

T.test("German countdown from the strings set", function()
	T.eq(BB.duration(0, DE), "<1 min")
	T.eq(BB.duration(7, DE), "7 min")
	T.eq(BB.duration(62, DE), "1 h 02 min")
	T.eq(BB.duration(26 * 60, DE), "1 d 2 h")
end)

T.test("the printing line is rebuilt from strings.left every update", function()
	local s = T.state("printing") -- endsAt − savedAt = 3720 s = 62 min
	local m = BB.model(s, s.savedAt)
	T.eq(m.stateLine, "1h 02m left")
	T.eq(m.countdown, "1h 02m")
	T.eq(m.leftWord, "left")
	m = BB.model(s, s.savedAt + 61)
	T.eq(m.stateLine, "1h 01m left") -- 3659 s → 60.98 → 61 min
	s.strings = DE
	m = BB.model(s, s.savedAt)
	T.eq(m.stateLine, "Noch 1 h 02 min")
	T.eq(m.leftWord, nil, "no lone 'Noch' under the countdown")
	s.strings.leftWord = "übrig"
	T.eq(BB.model(s, s.savedAt).leftWord, "übrig")
end)

T.test("fill() takes text literally", function()
	T.eq(BB.fill("{0} left", "%1 (50%)"), "%1 (50%) left")
	T.eq(BB.fill("{1} of {0}", "a", "b"), "b of a")
end)

T.test("the finish time comes out of detail, only while printing", function()
	local m = BB.model(T.state("printing"), T.state("printing").savedAt)
	T.eq(m.finish, "Done 5:15\226\128\175PM")
	T.eq(BB.model(T.state("paused"), 0).finish, nil)
	local s = T.state("printing")
	s.detail = "170/305" -- no finish known
	T.eq(BB.model(s, s.savedAt).finish, nil)
end)

T.done()
