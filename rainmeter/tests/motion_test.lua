-- The Rainmeter glue against a fake skin: what moves, when, and that nothing moves with motion off.
local T = require("lib")
local BB = T.BB

local function state(name, edit)
	local s = T.state(name)
	if edit then edit(s) end
	return s
end

local function boot(size, s)
	T.write(s)
	local skin = T.boot(size, T.TMP, s.savedAt)
	Update()
	return skin
end

local function step(s, seconds)
	T.write(s)
	T.unix = T.unix + (seconds or 1)
	T.clock = T.clock + (seconds or 1)
	Update()
end

T.test("first draw: no animation, content opaque, click target set", function()
	local skin = boot("Small", state("printing"))
	T.eq(next(BB.G.anims), nil)
	T.eq(skin:bangCount("Execute 1"), 0)
	T.ok(skin.options.Content.Shape:find("FFFFFFFF", 1, true))
	T.eq(skin.options.Card.LeftMouseUpAction, '["bambubar://printer/22E00A000000000"]')
	T.eq(skin.options.DialPct.Text, "64%")
	T.eq(skin.options.Ring.Hidden, "0")
end)

T.test("seconds pass: the countdown moves, nothing animates", function()
	local skin = boot("Small", state("printing"))
	for _ = 1, 61 do step(state("printing")) end
	T.eq(skin.options.Big.Text, "1h 01m")
	T.eq(next(BB.G.anims), nil)
	T.eq(skin:bangCount("Execute 1"), 0)
end)

T.test("state change: fade out 150 ms, swap, fade in 180 ms, ring tween 450 ms", function()
	local skin = boot("Small", state("printing"))
	step(state("paused", function(s) s.progress = 70 end))
	T.ok(BB.G.anims.fadeOut, "fading out")
	T.eq(skin:bangCount("Execute 1"), 1)
	T.eq(skin.options.Caption.Text, "Printing", "old content until the swap")
	-- Updates during the fade do not restart it.
	step(state("paused", function(s) s.progress = 70 end), 0)
	T.eq(skin:bangCount("Execute 1"), 1)
	T.clock = T.clock + 0.151
	Frame()
	T.eq(skin.options.Caption.Text, "Paused", "swapped")
	T.ok(BB.G.anims.fadeIn, "fading in")
	T.ok(BB.G.anims.progress, "ring tween")
	T.near(BB.G.anims.progress.from, 0.64, 1e-6)
	T.near(BB.G.anims.progress.to, 0.70, 1e-6)
	T.eq(BB.G.anims.progress.ms, 450)
	T.eq(BB.G.anims.fadeIn.ms, 180)
	T.runFrames()
	T.eq(next(BB.G.anims), nil, "settled")
	T.eq(skin.options.DialPct.Text, "70%")
	T.ok(skin.options.Content.Shape:find("FFFFFFFF", 1, true), "opaque again")
	T.eq(skin:bangCount("Stop 1"), 1, "frame timer stopped")
end)

T.test("finished: one pulse after the tween, then never again", function()
	local skin = boot("Small", state("printing"))
	step(state("finished"))
	T.clock = T.clock + 0.2
	Frame()
	T.runFrames(40) -- through the tween
	local pulses = 0
	for _ = 1, 100 do
		if BB.G.anims.pulse then pulses = pulses + 1; break end
		T.clock = T.clock + 0.016
		Frame()
	end
	T.eq(pulses, 1, "pulse started")
	T.eq(BB.G.anims.pulse.ms, 700)
	T.runFrames()
	T.eq(skin.options.Pulse.Hidden, "1")
	for _ = 1, 5 do step(state("finished")) end
	T.eq(BB.G.anims.pulse, nil, "no second pulse")
end)

T.test("a progress jump mid-tween retargets the running tween", function()
	local skin = boot("Medium", state("printing", function(s) s.progress = 40 end))
	step(state("printing"), 0) -- 40 → 64
	T.ok(BB.G.anims.progress)
	T.clock = T.clock + 0.2
	Frame()
	step(state("printing", function(s) s.progress = 80 end), 0)
	T.near(BB.G.anims.progress.to, 0.80, 1e-6)
	T.runFrames()
	T.eq(skin.options.Pct.Text, "80%")
end)

T.test("Medium pulses from the status dot", function()
	local skin = boot("Medium", state("printing"))
	step(state("finished"))
	T.clock = T.clock + 0.2
	Frame()
	T.runFrames()
	T.ok(skin.options.Pulse and skin.options.Pulse.Shape:find("Ellipse", 1, true), "pulse drawn")
end)

T.test("art changes crossfade over 300 ms; the light over 400 ms", function()
	local skin = boot("Medium", state("printing"))
	step(state("printing", function(s) s.art = "art-ab62b01c6c975d2d.png" end))
	T.ok(BB.G.anims.art, "crossfade")
	T.eq(BB.G.anims.art.ms, 300)
	T.eq(skin.options.ArtB.Hidden, "0")
	T.ok(skin.options.ArtB.ImageName:find("art-ab62b01c6c975d2d.png", 1, true))
	T.ok(skin.options.ArtA.ImageName:find("art-097fe53e6961850d.png", 1, true), "old art under it")
	step(state("printing", function(s) s.art = "art-ab62b01c6c975d2d.png" end), 0.1) -- a second passes mid-fade
	T.ok(skin.options.ArtA.ImageName:find("art-097fe53e6961850d.png", 1, true), "still the old art under it")
	T.runFrames()
	T.eq(skin.options.ArtB.Hidden, "1")
	T.ok(skin.options.ArtA.ImageName:find("art-ab62b01c6c975d2d.png", 1, true))
	boot("Large", state("printing"))
	step(state("printing", function(s) s.art = s.artUnlit; s.artUnlit = nil end))
	T.eq(BB.G.anims.art.ms, 400)
end)

T.test("connecting: the spinner runs on ActionList2 and stops when connected", function()
	local skin = boot("Small", state("connecting"))
	T.eq(skin:bangCount("Execute 2"), 1)
	local before = skin.options.Spinner.Shape2
	T.clock = T.clock + 0.3
	Spin()
	T.ok(skin.options.Spinner.Shape2 ~= before, "turned")
	step(state("connecting"))
	T.eq(skin:bangCount("Execute 2"), 1, "not restarted every second")
	step(state("idle"))
	T.runFrames()
	T.eq(skin:bangCount("Stop 2"), 1)
	T.eq(skin.options.Spinner.Hidden, "1")
end)

T.test("hover tweens the card over 140 ms", function()
	local skin = boot("Small", state("idle"))
	Hover(1)
	T.ok(BB.G.anims.hover)
	T.runFrames()
	T.ok(skin.options.Card.Shape2:find("FFFFFF38", 1, true), "border 22 %")
	T.ok(skin.options.Card.CardGradient:find("313337", 1, true), "lighter top")
	Hover(0)
	T.runFrames()
	T.ok(skin.options.Card.Shape2:find("FFFFFF14", 1, true), "border 8 %")
end)

T.test("motion off: no fades, tweens, pulses, crossfades or spinner", function()
	local off = function(s) s.motion = false end
	local skin = boot("Small", state("printing", off))
	step(state("paused", off))
	T.eq(next(BB.G.anims), nil)
	T.eq(skin.options.Caption.Text, "Paused", "swapped at once")
	step(state("finished", off))
	T.eq(next(BB.G.anims), nil, "no pulse")
	step(state("finished", function(s) s.motion = false; s.art = "art-other.png" end))
	T.eq(next(BB.G.anims), nil, "no crossfade")
	T.ok(skin.options.ArtA.Hidden ~= "0" or skin.options.ArtA.ImageName:find("art-other.png", 1, true))
	step(state("connecting", off))
	T.eq(skin.options.Spinner.Hidden, "0", "a static arc")
	local arc = skin.options.Spinner.Shape2
	step(state("connecting", off), 1)
	T.eq(skin.options.Spinner.Shape2, arc, "does not turn")
	Hover(1)
	T.eq(next(BB.G.anims), nil, "hover without a tween")
	T.ok(skin.options.Card.Shape2:find("FFFFFF38", 1, true))
	T.eq(skin:bangCount("Execute"), 0, "the animation timer never ran")
end)

T.test("an unreadable or broken file keeps the last good state", function()
	local skin = boot("Small", state("printing"))
	local f = io.open(T.TMP, "wb"); f:write("{ broken"); f:close()
	T.unix = T.unix + 1
	Update()
	T.eq(skin.options.Caption.Text, "Printing")
	os.remove(T.TMP)
	Update()
	T.eq(skin.options.Caption.Text, "Printing")
end)

T.test("no file at all: an 'Open Bambu Bar' card", function()
	os.remove(T.TMP)
	local skin = T.boot("Large", T.TMP .. ".missing", 1794323547)
	Update()
	T.eq(skin.options.Headline.Text, "Open Bambu Bar")
end)

T.done()
