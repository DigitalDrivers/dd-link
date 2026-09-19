-- Digital Drivers pit wall. Runs in the game of every driver on the race server and tells the server,
-- once a second, what only the game knows about the driver's own car: fuel, tyres, damage. The server
-- does not pass these messages on to other drivers; the platform shows them to the car's own team.

local telemetryEvent = ac.OnlineEvent({
  ac.StructItem.key('DD_Telemetry'),
  fuel = ac.StructItem.float(),
  maxFuel = ac.StructItem.float(),
  fuelPerLap = ac.StructItem.float(),
  engineLife = ac.StructItem.float(),
  brake = ac.StructItem.float(),
  tyreWear = ac.StructItem.array(ac.StructItem.float(), 4),
  tyreTemperature = ac.StructItem.array(ac.StructItem.float(), 4),
  tyrePressure = ac.StructItem.array(ac.StructItem.float(), 4),
  damage = ac.StructItem.array(ac.StructItem.float(), 4),
  inPitLane = ac.StructItem.boolean(),
}, function() end)

setInterval(function()
  local car = ac.getCar(0)
  if car == nil or not car.isConnected then return end
  local wheels = car.wheels
  telemetryEvent({
    fuel = car.fuel,
    maxFuel = car.maxFuel,
    fuelPerLap = car.fuelPerLap or 0,
    engineLife = car.engineLifeLeft,
    brake = car.brake,
    tyreWear = { wheels[0].tyreWear, wheels[1].tyreWear, wheels[2].tyreWear, wheels[3].tyreWear },
    tyreTemperature = { wheels[0].tyreCoreTemperature, wheels[1].tyreCoreTemperature, wheels[2].tyreCoreTemperature, wheels[3].tyreCoreTemperature },
    tyrePressure = { wheels[0].tyrePressure, wheels[1].tyrePressure, wheels[2].tyrePressure, wheels[3].tyrePressure },
    damage = { car.damage[0], car.damage[1], car.damage[2], car.damage[3] },
    inPitLane = car.isInPitlane,
  })
end, 1)
