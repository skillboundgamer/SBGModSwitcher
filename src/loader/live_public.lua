-- SBG public live.lua v2 - installed by the Mod Switcher as mod\live.lua (the loader chain: the Kratos loadout switched
-- On, or a preset with "loader": true) when the game folder has no live.lua of its own. Loads mod\sbg_loadout.lua (the
-- Valhalla loadout plug-in) and the files in PLUGINS (v2: mod\sbg_twogna.lua, two Gnas take turns): every 2 game
-- seconds it reads each file again, then runs its tick. A missing file is skipped (no tick; one log line).
-- No on-screen messages; log lines start with [SBG]. The probe tail mod\sbg_probe.lua loads this file.
-- The loadout block below is the same as switcher\loadout_stub.lua; the PLUGINS block is the same code with its own
-- state (SBG.PX[file]) and its own phase, so two files are never compiled in the same tick.
local L = {}
L.version = "public 3"

-- SBG-LOADOUT-STUB begin (added by the Mod Switcher, tab "Valhalla loadout": loads mod\sbg_loadout.lua every 2 game seconds; state in SBG.PL and SBG.LO; logs only)
local function sbgLoadoutTick(C, SBG)
  local PL = SBG.PL
  if not PL then PL = {clock = 99}; SBG.PL = PL end
  local dt = 0
  pcall(function() dt = C:GetUnitTime() or 0 end)
  PL.clock = PL.clock + dt
  if PL.clock >= 2 then
    PL.clock = 0
    local path = string.gsub(SBG.live or "mod\\live.lua", "live%.lua$", "sbg_loadout.lua")
    local fn, err = nil, nil
    if type(loadfile) == "function" then fn, err = loadfile(path) end
    local res = nil
    if fn then
      local ok, r = pcall(fn)
      if ok then res = r else err = r end
    end
    if type(res) == "table" and type(res.tick) == "function" then
      PL.mod = res
      if PL.ver ~= tostring(res.version) then PL.ver = tostring(res.version); print("[SBG] loadout file sbg_loadout.lua loaded, version " .. PL.ver) end
      PL.err = nil
    else
      PL.mod = nil
      err = tostring(err or "no tick function")
      if PL.err ~= err then PL.err = err; PL.ver = nil; print("[SBG] loadout file sbg_loadout.lua not loaded: " .. err) end
    end
  end
  if PL.mod then
    local ok, err = pcall(PL.mod.tick, C, SBG)
    if not ok and PL.tickErr ~= tostring(err) then PL.tickErr = tostring(err); print("[SBG] loadout ERR " .. PL.tickErr) end
  end
end
-- SBG-LOADOUT-STUB end

-- v2: more plug-in files next to live.lua. phase = game seconds until the first read (the loadout file is read on the
-- first tick, at 0, 2, 4 ...; sbg_twogna.lua at 1, 3, 5 ...): one compile per tick at most.
local PLUGINS = {
  {file = "sbg_twogna.lua", phase = 1},
  {file = "sbg_backblades.lua", phase = 0.5},   -- v3: Back Blades pack (cosmetic, stowed Blades of Chaos)
  {file = "sbg_axehide.lua", phase = 1.5},      -- v4: Hide the Leviathan Axe pack (cosmetic)
}

-- the loadout block's code for one file of PLUGINS; state in SBG.PX[file] (clock, mod, ver, err, tickErr)
local function sbgPluginTick(C, SBG, p)
  local PX = SBG.PX
  if not PX then PX = {}; SBG.PX = PX end
  local P = PX[p.file]
  if not P then P = {clock = 2 - p.phase}; PX[p.file] = P end
  local dt = 0
  pcall(function() dt = C:GetUnitTime() or 0 end)
  P.clock = P.clock + dt
  if P.clock >= 2 then
    P.clock = 0
    local path = string.gsub(SBG.live or "mod\\live.lua", "live%.lua$", p.file)
    local fn, err = nil, nil
    if type(loadfile) == "function" then fn, err = loadfile(path) end
    local res = nil
    if fn then
      local ok, r = pcall(fn)
      if ok then res = r else err = r end
    end
    if type(res) == "table" and type(res.tick) == "function" then
      P.mod = res
      if P.ver ~= tostring(res.version) then P.ver = tostring(res.version); print("[SBG] plug-in " .. p.file .. " loaded, version " .. P.ver) end
      P.err = nil
    else
      P.mod = nil
      err = tostring(err or "no tick function")
      if P.err ~= err then P.err = err; P.ver = nil; print("[SBG] plug-in " .. p.file .. " not loaded: " .. err) end
    end
  end
  if P.mod then
    local ok, err = pcall(P.mod.tick, C, SBG)
    if not ok and P.tickErr ~= tostring(err) then P.tickErr = tostring(err); print("[SBG] plug-in " .. p.file .. " ERR " .. P.tickErr) end
  end
end

local function gcAfterReload(st, before)
  -- the file was just compiled again (its clock was set back to exactly 0): free that garbage now, not frames
  -- later. Lua memory in Kratos' state is small; the v0.5j crash (10-21) was out of memory inside a reload.
  -- "== 0 and before ~= 0": a reload only (never every frame, also not on a negative time step or while paused).
  if st and st.clock == 0 and before ~= 0 and type(collectgarbage) == "function" then pcall(collectgarbage) end
end

L.tick = function(C, SBG)
  SBG.msgsOff = true -- the probe's own on-screen messages stay hidden (the log still gets them)
  local before = SBG.PL and SBG.PL.clock or 99
  sbgLoadoutTick(C, SBG)
  gcAfterReload(SBG.PL, before)
  for i = 1, #PLUGINS do
    local p = PLUGINS[i]
    local st = SBG.PX and SBG.PX[p.file]
    before = st and st.clock or 99
    sbgPluginTick(C, SBG, p)
    gcAfterReload(SBG.PX[p.file], before)
  end
end

return L
