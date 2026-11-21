local T = require("lib")
local BB = T.BB

local SIZES = { "Small", "Medium", "Large" }

-- Sections declared by a skin file and the files it includes.
local function sections(size)
	local found = {}
	local function scan(path)
		for line in T.read(path):gmatch("[^\r\n]+") do
			local name = line:match("^%[([^%]]+)%]")
			if name then found[name] = true end
			local inc = line:match("^@Include%d*=#@#(.+)$")
			if inc then scan(T.RES .. inc:gsub("\\", "/")) end
		end
	end
	scan(T.SKINS .. size .. "/" .. size .. ".ini")
	return found
end

local function shown(v, name) return v.out[name] and v.out[name].Hidden == "0" end

local function render(name, size, dt, edit)
	local s = T.state(name)
	if edit then edit(s) end
	local m = BB.model(s, s.savedAt + (dt or 0))
	return BB.render(m, size, T.ctx(), nil, 0), m
end

T.test("every meter the script sets is declared in the skin", function()
	for _, size in ipairs(SIZES) do
		local declared = sections(size)
		for _, extra in ipairs({ "Card", "Content", "Pulse", "ProbeLine", "ProbeWrap", "Shadow", "MeasureScript",
			"MeasureAnimation", "StyleText", "StyleShape", "StyleIcon", "StyleArt" }) do
			T.ok(declared[extra], size .. ": missing [" .. extra .. "]")
		end
		for _, name in ipairs(T.samples()) do
			local v = render(name, size)
			for meter in pairs(v.out) do
				T.ok(declared[meter], size .. "/" .. name .. ": the script sets [" .. meter .. "], not in the skin")
			end
		end
	end
end)

T.test("every sample renders in every size, options are strings, icons exist", function()
	for _, size in ipairs(SIZES) do
		for _, name in ipairs(T.samples()) do
			local v = render(name, size)
			for meter, o in pairs(v.out) do
				for k, x in pairs(o) do
					T.eq(type(x), "string", size .. "/" .. name .. " " .. meter .. "." .. k)
				end
				if o.SvgImage and o.Hidden == "0" then
					local file = o.SvgImage:gsub("^I/", "")
					T.ok(io.open(T.RES .. "Icons/" .. file, "rb"), "missing icon " .. file)
				end
			end
		end
	end
end)

T.test("Small: ring and countdown while printing", function()
	local v = render("printing", "Small")
	T.ok(shown(v, "Ring"))
	T.eq(v.out.DialPct.Text, "64%")
	T.eq(v.out.Caption.Text, "Printing")
	T.eq(v.out.Caption.StringCase, "Upper")
	T.eq(v.out.Big.Text, "1h 02m")
	T.eq(v.out.Sub.Text, "left")
	T.eq(v.out.FootTitle.Text, "Rear Dry Pod")
	T.ok(shown(v, "ActiveSpool"))
	T.eq(v.out.ActiveSpoolText.Text, "PLA Silk+ · A4")
	T.ok(not shown(v, "ArtA"), "no art in the dial while printing")
end)

T.test("Small: every state leads with what matters", function()
	local v = render("paused", "Small")
	T.eq(v.out.Reason.Text, "Filament ran out")
	T.ok(v.out.DialIcon.SvgImage:find("pause", 1, true))
	v = render("preparing", "Small")
	T.ok(v.out.DialIcon.SvgImage:find("fire", 1, true))
	T.ok(v.out.Ring.Shape4 and v.out.Ring.Shape4 ~= "", "heat ring")
	T.eq(v.out.FootDetail.Text, "Nozzle 164° → 220°")
	v = render("finished", "Small")
	T.ok(v.out.DialIcon.SvgImage:find("checkmark", 1, true))
	T.eq(v.out.Big.Text, "5m ago") -- the print that just finished is its own last job
	v = render("failed", "Small")
	T.ok(v.out.DialIcon.SvgImage:find("important", 1, true))
	v = render("failed", "Small", 0, function(s) s.canceled = true; s.tint = "#8E8E93" end)
	T.ok(v.out.DialIcon.SvgImage:find("dismiss", 1, true))
	v = render("idle", "Small")
	T.ok(shown(v, "ArtA"))
	T.eq(v.out.Temp1.Text, "27°")
	T.ok(shown(v, "Swatch4"))
	T.eq(v.out.FootDetail.Text, "Last: Benchy")
	v = render("connecting", "Small")
	T.ok(shown(v, "Spinner"))
	T.eq(v.out.FootTitle.Text, "P2S Workshop")
	v = render("unauthorized", "Small")
	T.eq(v.out.Caption.FontColor, BB.rgba(BB.C.paused, 1), "amber caption")
end)

T.test("Small: the printer's name over the footer, so two widgets tell their printers apart", function()
	for _, name in ipairs({ "printing", "preparing", "paused", "finished", "failed", "idle" }) do
		local v, m = render(name, "Small")
		T.ok(shown(v, "FootName"), name .. ": name shown")
		T.eq(v.out.FootName.Text, m.name, name)
		local below = shown(v, "Swatch1") and "Swatch1" or shown(v, "FootTitle") and "FootTitle" or "FootDetail"
		local nameY, titleY = tonumber(v.out.FootName.Y), tonumber(v.out[below].Y)
		T.ok(nameY < titleY, name .. ": above the footer")
		if shown(v, "ActiveSpool") then T.ok(tonumber(v.out.ActiveSpool.Y) + 16 <= nameY, name .. ": spool clear of it") end
	end
	local v = render("printing", "Small", 0, function(s) s.headline = s.name end)
	T.ok(not shown(v, "FootName"), "not twice when the job has no name")
	v = render("offline", "Small")
	T.ok(not shown(v, "FootName"), "offline already titles the footer with it")
	T.eq(v.out.FootTitle.Text, "P2S Workshop")
end)

T.test("Small: a finished print with no record of its end says Done", function()
	local v = render("finished", "Small", 0, function(s) s.lastJob = nil end)
	T.eq(v.out.Big.Text, "Done")
	v = render("failed", "Small", 0, function(s) s.lastJob = nil; s.strings.stoppedWord = "Gestoppt" end)
	T.eq(v.out.Big.Text, "Gestoppt")
end)

T.test("Small: a word too long for the column moves under the dial", function()
	local long = "Ошибка температуры нагревательной плиты"
	local v = render("paused", "Small", 0, function(s) s.reason = long end)
	T.ok(shown(v, "ReasonFull"), "reflowed")
	T.ok(not shown(v, "Reason"))
	T.eq(v.out.Sub.Text, "Layer 170/305")
	T.ok(not shown(v, "ActiveSpool"))
	v = render("paused", "Small", 0, function(s) s.reason = "Filament ran out" end)
	T.ok(not shown(v, "ReasonFull"))
end)

T.test("Small: a long countdown or caption shrinks, not below its floor", function()
	local v = render("printing", "Small", 0, function(s) s.strings.hoursMinutes = "{0} Std. {1} Min." end)
	local size = tonumber(v.out.Big.FontSize)
	T.ok(size < 15, "shrunk from 15 pt, got " .. size)
	T.ok(size >= 15 * 0.7 - 0.01, "not below 70 %")
	v = render("unauthorized", "Small", 0, function(s) s.headline = "ZUGANGSCODE ABGELEHNT" end)
	T.ok(tonumber(v.out.Caption.FontSize) >= 10.5 * 0.75 * 0.8 - 0.01, "caption floor 80 %")
end)

T.test("Medium: art, job, percentage, bar, finish and temperatures", function()
	local v = render("printing", "Medium")
	T.ok(shown(v, "ArtA"))
	T.ok(v.out.ArtA.ImageName:find("art-097fe53e6961850d.png", 1, true))
	T.eq(v.out.Name.Text, "P2S Workshop")
	T.eq(v.out.Status.Text, "Printing")
	T.eq(v.out.Headline.Text, "Rear Dry Pod")
	T.eq(v.out.Pct.Text, "64%")
	T.eq(v.out.Layers.Text, "170/305")
	T.ok(shown(v, "Bar"))
	T.eq(v.out.StateLine.Text, "1h 02m left")
	T.eq(v.out.Finish.Text, "Done 5:15\226\128\175PM")
	T.eq(v.out.Temp3.Text, "38°")
	T.ok(not shown(v, "Swatch1"))
end)

T.test("Medium: idle shows the loaded spools; not live shows no state dot", function()
	local v = render("idle", "Medium")
	T.ok(shown(v, "Swatch4"))
	T.eq(v.out.Line2.Text, "Last: Benchy")
	T.ok(not shown(v, "Bar"))
	v = render("offline", "Medium")
	T.ok(not shown(v, "Dot"))
	T.ok(not shown(v, "Swatch1"))
	v = render("unauthorized", "Medium")
	T.ok(v.out.HeaderIcon.SvgImage:find("key", 1, true))
	v = render("connecting", "Medium")
	T.ok(shown(v, "Spinner"))
end)

T.test("Large: tiles, AMS, footer; the info box when not live", function()
	local v = render("preparing", "Large")
	T.eq(v.out.TileValue1.Text, "164°")
	T.eq(v.out.TileTarget1.Text, "→ 220°")
	T.ok(not shown(v, "TileTarget2"), "bed at target: no arrow")
	T.eq(v.out.Tile1.ToolTipText, "Nozzle: 164° → 220°")
	T.eq(v.out.AmsTitle.Text, "AMS 2 Pro A")
	T.eq(v.out.AmsHum.Text, "16%")
	T.eq(v.out.SpoolMat4.Text, "PLA")
	T.ok(v.out.Spool4.ToolTipText:find("Gold", 1, true))
	v = render("paused", "Large")
	T.ok(v.out.FootText.Text:find("run out", 1, true))
	T.ok(v.out.FootIcon.SvgImage:find("warning", 1, true))
	v = render("idle", "Large")
	T.eq(v.out.FootText.Text, "Benchy")
	T.eq(v.out.FootRight.Text, "2h 00m ago")
	v = render("unconfigured", "Large")
	T.ok(shown(v, "InfoBox"))
	T.ok(v.out.InfoIcon.SvgImage:find("printer", 1, true))
	T.eq(v.out.InfoText.Text, "Open the app to connect your printer")
	T.ok(not shown(v, "Tile1"))
end)

T.test("multicolour spools get a gradient; empty slots a dashed ring", function()
	local o = BB.spoolOptions({ color = "#F7B32B", colors = { "#F7B32B", "#E86F9A", "#7D6BD6" }, fraction = 0.64 }, 36, true)
	T.eq(o.SpoolColors, "45 | F7B32BFF ; 0 | E86F9AFF ; 0.5 | 7D6BD6FF ; 1")
	local all = table.concat({ o.Shape, o.Shape2, o.Shape3 }, "\n")
	T.ok(all:find(BB.C.accent, 1, true), "active arc in accent")
	o = BB.spoolOptions({ empty = true, label = "A4" }, 36, false)
	T.ok(o.Shape2:find("StrokeDashes", 1, true))
	T.eq(o.Shape3, "")
end)

T.test("ring arcs: small and large arc flags, full circle, caps", function()
	local o = BB.ringOptions(47, 47, 62, 7, 0.25, "#21CC6B")
	T.ok(o.Shape4:find("Arc 47,19.5,74.5,47,27.5,27.5,0,0,0", 1, true), o.Shape4)
	o = BB.ringOptions(47, 47, 62, 7, 0.75, "#21CC6B")
	T.ok(o.Shape4:find(",0,0,1 |", 1, true), "large arc past half")
	o = BB.ringOptions(47, 47, 62, 7, 1, "#21CC6B")
	T.ok(o.Shape4:find("^Ellipse"), "full ring")
	o = BB.ringOptions(47, 47, 62, 7, 0, "#21CC6B")
	T.eq(o.Shape2, "")
end)

T.test("user text cannot inject Rainmeter variables", function()
	local v = render("printing", "Medium", 0, function(s) s.headline = "Bracket #2 [MeasureScript]" end)
	T.eq(v.out.Headline.Text, "Bracket [\\x23]2 [\\x5B]MeasureScript]")
end)

T.done()
