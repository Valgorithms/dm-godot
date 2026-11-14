/world
    name = "DM Godot compatibility smoke world"
    fps = 10
    turf = /turf/floor
    mob = /mob/player

/world/New()
    world << "boot"
    spawn(1)
        world << "spawn"
    fixture_step()
    world << "ready"

/proc/fixture_step()
    world << "sleep-start"
    sleep(2)
    world << "sleep-end"

/area/station
    name = "Smoke test station"

/turf
    icon = 'smoke.dmi'

/turf/floor
    name = "floor"
    icon_state = "floor"
    color = "#334155"

/turf/wall
    name = "wall"
    density = 1
    opacity = 1
    icon_state = "wall"
    color = "#94a3b8"

/mob/player
    name = "player"
    density = 1
    icon = 'smoke.dmi'
    icon_state = "player"
    color = "#38bdf8"
