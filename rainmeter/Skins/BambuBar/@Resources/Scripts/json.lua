-- A small JSON decoder for the widget state files (windows/docs/widget-state.md). Written for Bambu Bar.
-- decode(text) returns the value, or nil and a message. JSON null becomes json.null (a sentinel), so a
-- field that is null is told apart from one that is missing; use json.value(x) to turn it into nil.
-- Strings come back as UTF-8, \uXXXX escapes (with surrogate pairs) included.

local json = {}

json.null = setmetatable({}, { __tostring = function() return "null" end })

function json.value(x)
	if x == json.null then return nil end
	return x
end

local byte, sub, char, find = string.byte, string.sub, string.char, string.find
local floor = math.floor

local function utf8(code)
	if code < 0x80 then return char(code) end
	if code < 0x800 then return char(0xC0 + floor(code / 0x40), 0x80 + code % 0x40) end
	if code < 0x10000 then
		return char(0xE0 + floor(code / 0x1000), 0x80 + floor(code / 0x40) % 0x40, 0x80 + code % 0x40)
	end
	return char(0xF0 + floor(code / 0x40000), 0x80 + floor(code / 0x1000) % 0x40,
		0x80 + floor(code / 0x40) % 0x40, 0x80 + code % 0x40)
end

local escapes = { ['"'] = '"', ['\\'] = '\\', ['/'] = '/', b = '\b', f = '\f', n = '\n', r = '\r', t = '\t' }

local function fail(pos, what) error({ pos = pos, msg = what }, 0) end

local function skip(s, i)
	local _, e = find(s, "^[ \t\r\n]*", i)
	return e + 1
end

local value -- forward

local function str(s, i)
	-- s[i] is the opening quote.
	local parts, n = {}, 0
	local j = i + 1
	while true do
		local q = find(s, '["\\]', j)
		if not q then fail(i, "unterminated string") end
		if q > j then n = n + 1; parts[n] = sub(s, j, q - 1) end
		if byte(s, q) == 34 then return table.concat(parts), q + 1 end
		local e = sub(s, q + 1, q + 1)
		if e == "u" then
			local hex = sub(s, q + 2, q + 5)
			if not find(hex, "^%x%x%x%x$") then fail(q, "bad \\u escape") end
			local code = tonumber(hex, 16)
			j = q + 6
			if code >= 0xD800 and code <= 0xDBFF and sub(s, j, j + 1) == "\\u" then
				local low = tonumber(sub(s, j + 2, j + 5), 16)
				if low and low >= 0xDC00 and low <= 0xDFFF then
					code = 0x10000 + (code - 0xD800) * 0x400 + (low - 0xDC00)
					j = j + 6
				end
			end
			if code >= 0xD800 and code <= 0xDFFF then code = 0xFFFD end -- lone surrogate
			n = n + 1; parts[n] = utf8(code)
		else
			local c = escapes[e]
			if not c then fail(q, "bad escape") end
			n = n + 1; parts[n] = c
			j = q + 2
		end
	end
end

local function num(s, i)
	local _, e = find(s, "^-?%d+%.?%d*[eE]?[-+]?%d*", i)
	if not e then fail(i, "bad number") end
	local n = tonumber(sub(s, i, e))
	if not n then fail(i, "bad number") end
	return n, e + 1
end

local function arr(s, i)
	local out, n = {}, 0
	i = skip(s, i + 1)
	if byte(s, i) == 93 then return out, i + 1 end
	while true do
		local v
		v, i = value(s, i)
		n = n + 1; out[n] = v
		i = skip(s, i)
		local c = byte(s, i)
		if c == 93 then return out, i + 1 end
		if c ~= 44 then fail(i, "expected , or ]") end
		i = skip(s, i + 1)
	end
end

local function obj(s, i)
	local out = {}
	i = skip(s, i + 1)
	if byte(s, i) == 125 then return out, i + 1 end
	while true do
		if byte(s, i) ~= 34 then fail(i, "expected a key") end
		local k
		k, i = str(s, i)
		i = skip(s, i)
		if byte(s, i) ~= 58 then fail(i, "expected :") end
		local v
		v, i = value(s, skip(s, i + 1))
		out[k] = v
		i = skip(s, i)
		local c = byte(s, i)
		if c == 125 then return out, i + 1 end
		if c ~= 44 then fail(i, "expected , or }") end
		i = skip(s, i + 1)
	end
end

value = function(s, i)
	local c = byte(s, i)
	if c == 123 then return obj(s, i) end
	if c == 91 then return arr(s, i) end
	if c == 34 then return str(s, i) end
	if c == 45 or (c and c >= 48 and c <= 57) then return num(s, i) end
	if sub(s, i, i + 3) == "true" then return true, i + 4 end
	if sub(s, i, i + 4) == "false" then return false, i + 5 end
	if sub(s, i, i + 3) == "null" then return json.null, i + 4 end
	fail(i, c and "unexpected character" or "unexpected end")
end

function json.decode(text)
	if type(text) ~= "string" then return nil, "no text" end
	local i = 1
	if sub(text, 1, 3) == "\239\187\191" then i = 4 end -- UTF-8 BOM
	local ok, result, e = pcall(function()
		local v, j = value(text, skip(text, i))
		j = skip(text, j)
		if j <= #text then fail(j, "trailing characters") end
		return v, j
	end)
	if not ok then
		if type(result) == "table" then return nil, result.msg .. " at " .. result.pos end
		return nil, tostring(result)
	end
	return result
end

return json
