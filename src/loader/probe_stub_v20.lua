-- SBG probe loader stub v2.0 for mod\int9\gameart\scripts\characters\heroa00\main.lua. Ships no game code.
-- Runs the game's own Kratos script from the player's MIO dump ([dump] lua=1 writes it before this file runs),
-- then our probe tail mod\sbg_probe.lua (same _ENV). Undo: put versions/main_probe_v114.lua back here.
local ORIG = "mod\\dump\\int9\\gameart\\scripts\\characters\\heroa00\\main.lua"
local TAIL = "mod\\sbg_probe.lua"
local fn, err = loadfile(ORIG, "b", _ENV) -- "b": binary only, so a text file (this stub) is never loaded by mistake
if not fn then
  print("[SBG] probe stub v2.0: Kratos script NOT loaded: " .. tostring(err))
  error("SBG probe stub: no MIO dump of heroa00/main.lua. Set [dump] lua=1 in mod\\GOWR_MIO.ini or delete this file.")
end
local function after(...)
  print("[SBG] probe stub v2.0: Kratos script ran from the dump")
  local t, terr = loadfile(TAIL, "t", _ENV)
  if not t then
    print("[SBG] probe stub v2.0: sbg_probe.lua not loaded: " .. tostring(terr))
  else
    local ok, e = pcall(t)
    if not ok then print("[SBG] probe stub v2.0: sbg_probe.lua error: " .. tostring(e)) end
  end
  return ...
end
return after(fn(...))
