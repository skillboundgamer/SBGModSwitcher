-- ============================================================
-- SBG probe v1.15 (public tail): the Mod Switcher installs it as mod\sbg_probe.lua beside the loader stub
-- (versions/probe_stub_v20.lua), for the Kratos loadout of the public release (Straight To Tyr 1.2).
-- It only loads mod\live.lua: re-read every 2 game seconds, its tick runs every frame. No mode switch, no boss director,
-- no on-screen messages, no creature log (2026-10-21: "yes to both"; v1.14 keeps all of those for development).
-- Relative live.lua paths only (v1.14 also held an absolute path of one PC).
-- Kept from v1.14 for the plug-ins: SBG.time (game seconds since this Kratos load), SBG.frames, SBG.kratos, SBG.ver,
-- SBG.live (the live.lua path; the loadout loader makes mod\sbg_loadout.lua from it), SBG_API, SBG_LIVE_PATHS.
-- Nothing changes Kratos' speed/damage.
-- ============================================================
local SBG = {started = false, frames = 0, errShown = false, time = 0, ver = 1.15, msgsOff = true, liveClock = 99}
local LIVE_EVERY = 2.0 -- game seconds between two reads of mod\live.lua (99 above: the first read is on the first tick)
SBG_LIVE_PATHS = {"mod\\live.lua", "mod/live.lua"} -- relative to the game folder (the game's working folder)
SBG_API = SBG
local function sbgPrint(s) print("[SBG] " .. tostring(s)) end

local function liveTick(C)
  if not SBG.live then return end
  if type(loadfile) ~= "function" then if not SBG.noLoadfile then SBG.noLoadfile = true; sbgPrint("live: loadfile missing") end; return end
  local fn, err = loadfile(SBG.live)
  if not fn then
    if SBG.liveErr ~= tostring(err) then SBG.liveErr = tostring(err); sbgPrint("live.lua load: " .. SBG.liveErr) end
    return
  end
  local ok, res = pcall(fn)
  if not ok then
    if SBG.liveErr ~= tostring(res) then SBG.liveErr = tostring(res); sbgPrint("live.lua error: " .. SBG.liveErr) end -- once per error
    return
  end
  local v = (type(res) == "table") and res.version or res
  if v ~= SBG.liveVer then
    SBG.liveVer = v
    sbgPrint("live.lua loaded, version " .. tostring(v))
    if type(res) == "table" and type(res.onLoad) == "function" then pcall(res.onLoad, C, SBG) end
  end
  SBG.liveFn = (type(res) == "table") and res.tick or nil
end

local function SBG_Probe(C)
  if not SBG.started then
    SBG.started = true
    sbgPrint("probe v1.15 alive")
    SBG.live = nil
  end
  SBG.frames = SBG.frames + 1
  SBG.kratos = C
  local dt = 0 -- seconds of game time since the last tick (0 while paused); frames are not a clock: 30 to 400 per second
  pcall(function() dt = C:GetUnitTime() end)
  if type(dt) ~= "number" or dt < 0 or dt > 0.5 then dt = 0 end
  SBG.time = SBG.time + dt
  SBG.liveClock = SBG.liveClock + dt
  if SBG.liveClock >= LIVE_EVERY then -- game seconds, not frames (30 to 400 frames per second)
    SBG.liveClock = 0
    if not SBG.live then -- found once, kept; missing or broken: looked for again every LIVE_EVERY seconds (v1.14 never did)
      local why = "not found"
      for _, p in ipairs(SBG_LIVE_PATHS) do
        local ok, f, e = pcall(loadfile, p)
        if ok and f then SBG.live = p; break end
        if ok and e and not string.find(tostring(e), "No such file", 1, true) then why = tostring(e) end -- there, but broken
      end
      local line = "live file: " .. tostring(SBG.live) .. (SBG.live and "" or " (" .. why .. ")")
      if line ~= SBG.liveMissLine then SBG.liveMissLine = line; sbgPrint(line) end -- once per state, not every 2 s
    end
    local ok, err = pcall(liveTick, C)
    if not ok and SBG.liveTickErr ~= tostring(err) then SBG.liveTickErr = tostring(err); sbgPrint("liveTick error: " .. SBG.liveTickErr) end
    if type(collectgarbage) == "function" then pcall(collectgarbage) end -- free the live.lua compile garbage in this tick
  end
  if SBG.liveFn then
    local ok, err = pcall(SBG.liveFn, C, SBG)
    if not ok then
      if SBG.fnErr ~= tostring(err) then SBG.fnErr = tostring(err); sbgPrint("live fn error: " .. SBG.fnErr) end -- once per error
      SBG.liveFn = nil
    end
  end
end

local SBG_origUpdate = OnCreatureUpdate
function OnCreatureUpdate(C)
  SBG_origUpdate(C)
  local ok, err = pcall(SBG_Probe, C)
  if not ok and not SBG.errShown then
    SBG.errShown = true
    sbgPrint("probe error: " .. tostring(err))
  end
end
sbgPrint("wrapper installed (v1.15)")
