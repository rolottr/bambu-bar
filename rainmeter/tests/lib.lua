-- Test helpers for the widget skins: a tiny assertion set, the samples, stubbed Rainmeter globals, and a text
-- measurer that stands in for DirectWrite (about 0.56 em per character, 1.33 em per line).

local T = {}

T.ROOT = assert(os.getenv("BB_ROOT"), "run through tests/run.sh")
T.RES = T.ROOT .. "/Skins/BambuBar/@Resources/"
T.SKINS = T.ROOT .. "/Skins/BambuBar/"
T.SAMPLES = T.ROOT .. "/samples/"

local failures, count, current = 0, 0, "?"

function T.test(name, fn)
	current = name
	count = count + 1
	local ok, err = xpcall(fn, debug.traceback)
	if ok then
		io.write("  ok   ", name, "\n")
	else
		failures = failures + 1
		io.write("  FAIL ", name, "\n       ", tostring(err):gsub("\n", "\n       "), "\n")
	end
end

local function show(v)
	if type(v) == "string" then return string.format("%q", v) end
	return tostring(v)
end

function T.eq(actual, expected, what)
	if actual ~= expected then
		error((what and (what .. ": ") or "") .. "expected " .. show(expected) .. ", got " .. show(actual), 2)
	end
end

function T.near(actual, expected, eps, what)
	if type(actual) ~= "number" or math.abs(actual - expected) > (eps or 1e-9) then
		error((what and (what .. ": ") or "") .. "expected ~" .. show(expected) .. ", got " .. show(actual), 2)
	end
end

function T.ok(v, what)
	if not v then error(what or "expected a true value", 2) end
end

function T.done()
	io.write(string.format("  %d tests, %d failed\n", count, failures))
	os.exit(failures == 0 and 0 or 1)
end

function T.read(path)
	local f = assert(io.open(path, "rb"))
	local s = f:read("*a")
	f:close()
	return s
end

function T.samples()
	local names = {}
	local p = io.popen('ls "' .. T.SAMPLES .. '"')
	for line in p:lines() do
		if line:match("%.json$") then names[#names + 1] = line:gsub("%.json$", "") end
	end
	p:close()
	table.sort(names)
	return names
end

-- ----------------------------------------------------------------------------- the scripts

T.json = dofile(T.RES .. "Scripts/json.lua")
dofile(T.RES .. "Scripts/widget.lua")
BB.json = T.json
T.BB = BB

function T.state(name)
	local s = assert(T.json.decode(T.read(T.SAMPLES .. name .. ".json")))
	return BB.clean(s, T.json.null)
end

function T.copy(t)
	if type(t) ~= "table" then return t end
	local c = {}
	for k, v in pairs(t) do c[k] = T.copy(v) end
	return c
end

-- Stand-in for DirectWrite: width from the character count, height from greedy word wrapping.
function T.measure(fo, text, w)
	local px = tonumber(fo.FontSize) / 0.75
	local cw = px * 0.56
	local function width(s) return BB.utf8len(s) * cw end
	local lineH = px * 1.33
	if not w then return width(text), lineH end
	local lines, cur = 1, 0
	for word in text:gmatch("%S+") do
		local ww = width(word)
		if cur == 0 then
			cur = ww
			while cur > w do lines = lines + 1; cur = cur - w end
		elseif cur + cw + ww <= w then
			cur = cur + cw + ww
		else
			lines = lines + 1
			cur = ww
		end
	end
	return math.min(w, width(text)), lines * lineH
end

function T.ctx()
	return { measure = T.measure, faces = BB.faces, iconDir = "I/", artDir = "A/", lineCache = {} }
end

-- ----------------------------------------------------------------------------- Rainmeter stubs

-- A fake skin: records options and bangs, measures the probe meters with T.measure.
function T.fakeSkin(vars)
	local skin = { options = {}, bangs = {}, vars = vars or {} }
	function skin:SetOption(section, key, value)
		self.options[section] = self.options[section] or {}
		self.options[section][key] = value
	end
	function skin:Bang(...)
		self.bangs[#self.bangs + 1] = table.concat({ ... }, " ")
	end
	function skin:GetMeter(name)
		local o = self.options[name] or {}
		return {
			GetW = function() local w = T.measure(o, o.Text or "", tonumber(o.W)); return w end,
			GetH = function() local _, h = T.measure(o, o.Text or "", tonumber(o.W)); return h end,
		}
	end
	function skin:ReadTextFile(path)
		local f = io.open(path, "rb")
		if not f then return nil, "Unable to read file: " .. path end
		local s = f:read("*a")
		f:close()
		return s
	end
	function skin:ReplaceVariables(s)
		s = s:gsub("#@#", T.RES)
		return (s:gsub("#([%w_]+)#", function(name) return self.vars[name] end))
	end
	function skin:GetVariable(name, default) return self.vars[name] or default end
	function skin:bangCount(pattern)
		local n = 0
		for _, b in ipairs(self.bangs) do if b:find(pattern, 1, true) then n = n + 1 end end
		return n
	end
	return skin
end

function T.fakeSelf(size)
	return { GetOption = function(_, key, default) if key == "Size" then return size end return default end }
end

-- Loads a skin of `size` reading `file`, at unix time `now` and animation clock `clock`.
function T.boot(size, file, now)
	SKIN = T.fakeSkin({ StateFile = file })
	SELF = T.fakeSelf(size)
	BB.now = function() return T.unix end
	BB.clock = function() return T.clock end
	T.unix = now or 1794323547
	T.clock = 0
	Initialize()
	return SKIN
end

-- Writes a state to a temp file the fake skin reads.
T.TMP = assert(os.getenv("BB_TMP"), "run through tests/run.sh") .. "/state.json"
function T.write(state)
	local f = assert(io.open(T.TMP, "wb"))
	f:write(T.encode(state))
	f:close()
end

-- A minimal encoder for the tests (the skins only decode).
function T.encode(v)
	local t = type(v)
	if v == nil or v == T.json.null then return "null" end
	if t == "boolean" or t == "number" then return tostring(v) end
	if t == "string" then
		return '"' .. v:gsub('[%c"\\]', function(c) return string.format("\\u%04x", c:byte()) end) .. '"'
	end
	if #v > 0 or next(v) == nil then
		local parts = {}
		for i, x in ipairs(v) do parts[i] = T.encode(x) end
		return "[" .. table.concat(parts, ",") .. "]"
	end
	local parts = {}
	for k, x in pairs(v) do parts[#parts + 1] = T.encode(k) .. ":" .. T.encode(x) end
	return "{" .. table.concat(parts, ",") .. "}"
end

-- Runs Frame() until the animations settle (or `limit` frames), 16 ms apart.
function T.runFrames(limit)
	for _ = 1, limit or 200 do
		if next(BB.G.anims) == nil then return end
		T.clock = T.clock + 0.016
		Frame()
	end
end

return T
