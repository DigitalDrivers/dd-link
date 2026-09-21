-- Digital Drivers notices: messages of race control and what the platform has to tell a driver, shown as a
-- banner at the top of the screen for a few seconds, one after the other. The server sends them; the same
-- words go to the chat as well, so they can be read again.

local colours = {
  [0] = rgbm(1, 0.302, 0.180, 1),   -- race control: the accent of the brand
  [1] = rgbm(0.424, 0.769, 1, 1),   -- information
  [2] = rgbm(0.239, 0.863, 0.518, 1), -- result
  [3] = rgbm(1, 0.710, 0.278, 1),   -- warning
}
local showSeconds = 9
local fadeSeconds = 0.35
local queue = {}
local current = nil
local shownFor = 0

-- Splits a text into lines of at most `limit` characters, at spaces where there are any.
local function wrap(text, limit)
  local lines, line = {}, ''
  for word in text:gmatch('%S+') do
    if #line > 0 and #line + 1 + #word > limit then
      table.insert(lines, line)
      line = word
    else
      line = #line > 0 and line .. ' ' .. word or word
    end
  end
  if #line > 0 then table.insert(lines, line) end
  return lines
end

ac.OnlineEvent({
  ac.StructItem.key('DD_Notice'),
  kind = ac.StructItem.byte(),
  title = ac.StructItem.string(32),
  text = ac.StructItem.string(160),
}, function (sender, message)
  -- Only the server sends notices, never another driver.
  if sender ~= nil then return end
  table.insert(queue, { kind = message.kind, title = message.title, lines = wrap(message.text, 64) })
end)

local ready = ac.OnlineEvent({
  ac.StructItem.key('DD_NoticesReady'),
  dummy = ac.StructItem.byte(),
}, function () end)
ready({})

function script.update(dt)
  if current == nil and #queue > 0 then
    current = table.remove(queue, 1)
    shownFor = 0
  end
  if current ~= nil then
    shownFor = shownFor + dt
    if shownFor > showSeconds then current = nil end
  end
end

function script.drawUI()
  if current == nil then return end
  local alpha = math.max(0, math.min(1, shownFor / fadeSeconds, (showSeconds - shownFor) / fadeSeconds))
  local sim = ac.getSim()
  local width = math.min(620, sim.windowWidth - 40)
  local height = 50 + 24 * #current.lines
  local topLeft = vec2((sim.windowWidth - width) / 2, 90)
  local colour = colours[current.kind] or colours[1]
  ui.drawRectFilled(topLeft, topLeft + vec2(width, height), rgbm(0.055, 0.067, 0.086, 0.92 * alpha))
  ui.drawRectFilled(topLeft, topLeft + vec2(6, height), rgbm(colour.r, colour.g, colour.b, alpha))
  ui.pushDWriteFont('Segoe UI;Weight=Bold')
  ui.dwriteDrawText(current.title, 18, topLeft + vec2(22, 12), rgbm(colour.r, colour.g, colour.b, alpha))
  ui.popDWriteFont()
  ui.pushDWriteFont('Segoe UI')
  for i, line in ipairs(current.lines) do
    ui.dwriteDrawText(line, 17, topLeft + vec2(22, 16 + 24 * i), rgbm(0.957, 0.965, 0.973, alpha))
  end
  ui.popDWriteFont()
end
