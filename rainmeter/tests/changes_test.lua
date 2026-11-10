local T = require("lib")
local BB = T.BB

local function model(name, dt, edit)
	local s = T.state(name)
	if edit then edit(s) end
	return BB.model(s, s.savedAt + (dt or 0))
end

T.test("the first model is a first, not a change", function()
	local c = BB.changes(nil, model("printing"), "Small")
	T.eq(c.first, true)
	T.eq(c.state, nil)
end)

T.test("nothing fires between two seconds of the same print", function()
	local a, b = model("printing", 0), model("printing", 1)
	local c = BB.changes(a, b, "Small")
	T.eq(c.state, false)
	T.eq(c.finished, false)
	T.eq(c.progress, nil)
	T.eq(c.art, nil)
end)

T.test("a state change fires once: on the change, not on the seconds after", function()
	local seq = { model("printing", 0), model("paused", 1), model("paused", 2), model("paused", 3),
		model("printing", 4), model("printing", 5) }
	local fired = 0
	for i = 2, #seq do
		if BB.changes(seq[i - 1], seq[i], "Medium").state then fired = fired + 1 end
	end
	T.eq(fired, 2)
end)

T.test("link and canceled count as state changes", function()
	T.eq(BB.changes(model("idle"), model("offline"), "Small").state, true)
	T.eq(BB.changes(model("offline"), model("connecting"), "Small").state, true)
	local canceled = model("failed", 0, function(s) s.canceled = true end)
	T.eq(BB.changes(model("failed"), canceled, "Small").state, true)
end)

T.test("finished fires exactly once", function()
	local seq = { model("printing"), model("finished"), model("finished", 1), model("finished", 2) }
	local fired = 0
	for i = 2, #seq do
		if BB.changes(seq[i - 1], seq[i], "Small").finished then fired = fired + 1 end
	end
	T.eq(fired, 1)
	T.eq(BB.changes(model("offline"), model("finished"), "Small").finished, true)
end)

T.test("a jump in progress tweens; a one-second creep does not", function()
	local c = BB.changes(model("printing", 0, function(s) s.progress = 40 end), model("printing", 0), "Medium")
	T.near(c.progress.from, 0.40)
	T.near(c.progress.to, 0.64)
	T.eq(BB.changes(model("printing", 0), model("printing", 30), "Medium").progress, nil)
end)

T.test("art: 300 ms when the picture changes, 400 ms when only the light switched", function()
	local lit = model("printing")
	local grown = model("printing", 0, function(s) s.art = "art-other.png" end)
	local tr = BB.changes(lit, grown, "Large").art
	T.eq(tr.ms, 300)
	T.eq(tr.light, false)
	T.eq(tr.to, "art-other.png")
	-- The light goes off: the new art is the old file's artUnlit.
	local dark = model("printing", 0, function(s) s.art = s.artUnlit; s.artUnlit = nil end)
	tr = BB.changes(lit, dark, "Large").art
	T.eq(tr.ms, 400)
	T.eq(tr.light, true)
	-- And on again.
	tr = BB.changes(dark, lit, "Large").art
	T.eq(tr.ms, 400)
end)

T.done()
