local T = require("lib")
local BB = T.BB

T.test("a file up to 30 minutes old is shown as written", function()
	local s = T.state("printing")
	local m = BB.model(s, s.savedAt + 30 * 60)
	T.eq(m.stale, false)
	T.eq(m.live, true)
	T.eq(m.link, "online")
end)

T.test("online but older than 30 minutes: the offline look", function()
	local s = T.state("printing")
	local m = BB.model(s, s.savedAt + 30 * 60 + 1)
	T.eq(m.stale, true)
	T.eq(m.live, false)
	T.eq(m.link, "offline")
	T.eq(m.state, "idle")
	T.eq(m.active, false)
	T.eq(m.progress, 0)
	T.eq(#m.temps, 0)
	T.eq(#m.spools, 0)
	T.eq(m.caption, "Offline")
	T.eq(m.captionColor, BB.C.sec)
	T.eq(m.stateLine, "Benchy") -- the last job, without a translated "Last:" (not in the contract yet)
	for _, size in ipairs({ "Small", "Medium", "Large" }) do
		local v = BB.render(m, size, T.ctx(), 0, 0)
		T.eq(v.out.ArtA.Greyscale, "1", size .. " art dimmed")
		T.eq(v.out.ArtA.ImageAlpha, "140", size .. " art alpha")
	end
end)

T.test("the app's own offline texts win when it writes them", function()
	local s = T.state("printing")
	s.strings.offline = "Не в сети"
	s.offline = { stateLine = "Последняя: Benchy", info = "Принтер снова появится здесь…" }
	local m = BB.model(s, s.savedAt + 3600)
	T.eq(m.caption, "Не в сети")
	T.eq(m.stateLine, "Последняя: Benchy")
	T.eq(m.info, "Принтер снова появится здесь…")
end)

T.test("connecting goes stale too (the app stopped mid-reconnect); offline and the rest do not", function()
	local s = T.state("connecting")
	T.eq(BB.model(s, s.savedAt + 3600).stale, true)
	for _, name in ipairs({ "offline", "unauthorized", "unconfigured" }) do
		local x = T.state(name)
		local m = BB.model(x, x.savedAt + 99999)
		T.eq(m.stale, false, name)
		T.eq(m.link, x.link, name)
	end
end)

T.test("a newer contract asks for an update", function()
	local s = T.state("printing")
	s.v = 2
	local m = BB.model(s, s.savedAt)
	T.eq(m.live, false)
	T.eq(m.headline, "Update Bambu Bar")
end)

T.done()
