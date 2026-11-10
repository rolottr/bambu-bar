local T = require("lib")
local BB = T.BB

local function printing(progress, updatedAt, endsAt)
	return { state = "printing", progress = progress, updatedAt = updatedAt, endsAt = endsAt }
end

T.test("projection: progress at updatedAt, linear to 100 at endsAt", function()
	local s = printing(40, 1000, 2000)
	T.near(BB.projectedProgress(s, 1000), 0.40)
	T.near(BB.projectedProgress(s, 1500), 0.70)
	T.near(BB.projectedProgress(s, 900), 0.40, 1e-9, "before updatedAt")
end)

T.test("projection is capped at 99 %", function()
	local s = printing(40, 1000, 2000)
	T.near(BB.projectedProgress(s, 2000), 0.99)
	T.near(BB.projectedProgress(s, 99999), 0.99)
	T.near(BB.projectedProgress(printing(100, 1000, 2000), 1000), 0.99, 1e-9, "even from 100")
end)

T.test("projection only while printing", function()
	for _, st in ipairs({ "paused", "preparing", "finished", "failed", "idle" }) do
		local s = printing(64, 1000, 2000)
		s.state = st
		T.near(BB.projectedProgress(s, 1900), 0.64, 1e-9, st)
	end
	T.near(BB.projectedProgress(printing(64, 1000, nil), 1900), 0.64, 1e-9, "no endsAt")
	T.near(BB.projectedProgress(printing(64, 2000, 2000), 1900), 0.64, 1e-9, "endsAt not after updatedAt")
end)

T.test("the samples project as the app does", function()
	local s = T.state("printing")
	local m = BB.model(s, s.savedAt)
	T.eq(m.percent, 64)
	m = BB.model(s, s.savedAt + 1500) -- 64 + 36 * 1500 / 3720 = 78.5 (and still fresh: < 30 min)
	T.eq(m.percent, 78)
	m = BB.model(T.state("paused"), T.state("paused").savedAt + 1500)
	T.eq(m.percent, 64)
	T.eq(BB.model(T.state("offline"), 0).progress, 0)
end)

T.test("minutes left round up, never below 0", function()
	local s = printing(10, 0, 3600)
	T.eq(BB.minutesLeft(s, 0), 60)
	T.eq(BB.minutesLeft(s, 1), 60) -- 59.98 → 60
	T.eq(BB.minutesLeft(s, 59), 60)
	T.eq(BB.minutesLeft(s, 60), 59)
	T.eq(BB.minutesLeft(s, 3599), 1)
	T.eq(BB.minutesLeft(s, 3600), 0)
	T.eq(BB.minutesLeft(s, 9999), 0)
end)

T.test("paused shows remainingMinutes as written; other states none", function()
	local s = T.state("paused")
	T.eq(BB.minutesLeft(s, s.savedAt + 600), 62)
	T.eq(BB.minutesLeft(T.state("finished"), 0), nil)
	T.eq(BB.minutesLeft(T.state("preparing"), 0), nil)
end)

T.test("dial value: heat while preparing, full when finished (Small); progress elsewhere", function()
	local m = BB.model(T.state("preparing"), T.state("preparing").savedAt)
	T.near(BB.dialValue(m, "Small"), 164 / 220, 1e-9)
	T.near(BB.dialValue(m, "Medium"), 0)
	m = BB.model(T.state("finished"), 0)
	T.eq(BB.dialValue(m, "Small"), 1)
	m = BB.model(T.state("idle"), T.state("idle").savedAt)
	T.eq(BB.dialValue(m, "Small"), 0)
end)

T.done()
