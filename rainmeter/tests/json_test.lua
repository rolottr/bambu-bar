local T = require("lib")
local json = T.json

T.test("every sample parses, with the contract's fields", function()
	local names = T.samples()
	T.ok(#names >= 11, "expected the 11 sample scenes, found " .. #names)
	for _, name in ipairs(names) do
		local s, err = json.decode(T.read(T.SAMPLES .. name .. ".json"))
		T.ok(s, name .. ": " .. tostring(err))
		T.eq(s.v, 1, name .. " v")
		T.eq(type(s.savedAt), "number", name .. " savedAt")
		T.eq(type(s.strings), "table", name .. " strings")
		T.ok(s.link and s.state and s.caption and s.headline, name .. " texts")
		T.ok(s.art == json.null or type(s.art) == "string", name .. " art")
	end
end)

T.test("art named in each sample exists next to it", function()
	for _, name in ipairs(T.samples()) do
		local s = T.state(name)
		for _, f in ipairs({ s.art, s.artUnlit }) do
			T.ok(io.open(T.SAMPLES .. f, "rb"), name .. ": missing " .. f)
		end
	end
end)

T.test("\\u escapes become UTF-8, surrogate pairs included", function()
	local s = T.state("printing")
	T.eq(s.detail, "Done 5:15\226\128\175PM · 170/305") -- U+202F narrow no-break space
	T.eq(s.spools[1].amount, "≈\226\128\137810 g") -- U+2009 thin space
	T.eq(json.decode('"\\ud83d\\udda8"'), "\240\159\150\168") -- U+1F5A8 printer
	T.eq(json.decode('"a\\n\\"b\\"\\\\/\\/"'), 'a\n"b"\\//')
end)

T.test("null is a sentinel, and clean() turns it into nil", function()
	local raw = json.decode('{"a":null,"b":[1,2],"c":{"d":null,"e":false}}')
	T.eq(raw.a, json.null)
	T.eq(json.value(raw.a), nil)
	local s = T.BB.clean(raw, json.null)
	T.eq(s.a, nil)
	T.eq(s.c.d, nil)
	T.eq(s.c.e, false)
	T.eq(#s.b, 2)
end)

T.test("numbers, literals and whitespace", function()
	local s = json.decode(' { "i" : -12 , "f" : 1.5e2 , "t" : true , "z" : 0 } ')
	T.eq(s.i, -12); T.eq(s.f, 150); T.eq(s.t, true); T.eq(s.z, 0)
	T.eq(#json.decode("[]"), 0)
	T.eq(json.decode("\239\187\191[1]")[1], 1) -- BOM
end)

T.test("malformed JSON returns nil and a message", function()
	for _, bad in ipairs({ "", "{", '{"a":}', '{"a":1,}', "[1 2]", '"\\x"', '{"a":1} x', "nul" }) do
		local v, err = json.decode(bad)
		T.eq(v, nil, bad)
		T.ok(type(err) == "string", "message for " .. bad)
	end
end)

T.done()
