-- Keeps a list of the files open in this Aseprite instance (one path per line) for
-- DesktopSessionManager. Aseprite exposes open tabs only to scripts, not in its title or files.
-- The file name must match ProjectDetector.AsepriteListFile.
local listFile = app.fs.joinPath(app.fs.userConfigPath, "desktop-session-open-files.txt")
local last = nil
local disabled = false
local listeners = {}

local function save(text)
  -- Aseprite asks once before a script writes files. If that is denied, stop instead of asking
  -- again on every site change.
  local ok = pcall(function()
    local f = assert(io.open(listFile, "w"))
    f:write(text)
    f:close()
  end)
  if not ok then disabled = true end
  return ok
end

local function update()
  if disabled then return end
  local names = {}
  for _, sprite in ipairs(app.sprites) do
    if app.fs.isFile(sprite.filename) then table.insert(names, sprite.filename) end
  end
  local text = table.concat(names, "\n")
  if text ~= last and save(text) then last = text end
end

function init(plugin)
  -- Written right away so the list is newer than this process; older lists are ignored.
  update()
  listeners = {
    app.events:on("sitechange", update),   -- tab opened, closed or switched
    app.events:on("aftercommand", update), -- Save As renames the open file
  }
end

function exit(plugin)
  for _, id in ipairs(listeners) do app.events:off(id) end
  if not disabled then save("") end
end
