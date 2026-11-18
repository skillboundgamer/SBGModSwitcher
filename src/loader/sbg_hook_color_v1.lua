-- SBG hook v1 (2026-11-18) for mod\int9\gameart\scripts\libraries\core\color.lua. Ships no game code.
-- Why: VOTG owns Kratos' main.lua. Kratos' script (the game's own and VOTG's) requires "core.color" (also through
--   design.DesignerLibrary), and VOTG ships no core/color.lua, so this loose file runs in Kratos' Lua state without any
--   VOTG file being touched. It runs the game's own core.color from the player's dump and returns it unchanged. Then it
--   arms one game thunk (core.thunk, the way the game's creature.timer ticks) on OnCreatureUpdate: on the first tick it
--   loads our probe tail mod\sbg_probe.lua, which wraps OnCreatureUpdate as before.
-- Never breaks the state it runs in: no dump = our own fallback table, every SBG step runs in pcall, once per Lua state,
--   and nothing is done when the probe already runs (an older Kratos stub of ours put back by VOTG's uninstall).
local DUMP = "mod\\dump\\int9\\gameart\\scripts\\libraries\\core\\color.lua"
local TAIL = "mod\\sbg_probe.lua"
local function log(s) pcall(print, "[SBG] hook v1: " .. s) end
local mod
local fn, err = loadfile(DUMP, "b", _ENV) -- "b": binary only, so this text file is never loaded by mistake
if fn then
  local ok, res = pcall(fn, ...)
  if ok and type(res) == "table" then mod = res else log("game colours failed: " .. tostring(res)) end
else
  log("no dump of core.color (" .. tostring(err) .. "): fallback colours")
end
if mod == nil then mod = setmetatable({}, {__index = function() return 16777215 end}) end
pcall(function()
  if rawget(_G, "SBG_HOOK") ~= nil then return end
  if type(rawget(_G, "__hook_thunks")) ~= "table" then rawset(_G, "SBG_HOOK", "no thunks"); return end -- not a creature state
  rawset(_G, "SBG_HOOK", "armed")
  require("core.thunk").Install("OnCreatureUpdate", function(C)
    if rawget(_G, "SBG_HOOK") ~= "armed" then return end
    rawset(_G, "SBG_HOOK", "done")
    if rawget(_G, "SBG_API") ~= nil then log("probe already runs (Kratos stub)"); return end
    if type(rawget(_G, "OnCreatureUpdate")) ~= "function" then log("no OnCreatureUpdate here"); return end
    local t, terr = loadfile(TAIL, "t", _ENV)
    if not t then log("sbg_probe.lua not loaded: " .. tostring(terr)); return end
    local ok, e = pcall(t)
    log(ok and "probe tail loaded" or ("probe tail error: " .. tostring(e)))
  end)
end)
return mod
