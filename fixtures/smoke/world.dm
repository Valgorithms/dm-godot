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

/mob/player
    var/map_visits = 0
    proc/map_visit()
        map_visits += 1
        return map_visits

/proc/test_mapped_instance(mob/player/entity, turf/tile, area/region)
    world << "links=[entity.loc == tile],[tile.loc == region],[region.loc == null]"
    world << "coordinates=[entity.x],[entity.y],[entity.z]"
    world << "visits=[entity.map_visit()]"

/proc/test_map_coordinates(atom/item)
    world << "coordinates=[item.x],[item.y],[item.z]"

/proc/test_map_contents(atom/item)
    world << item.contents

/proc/test_map_overlays(atom/item)
    world << item.overlays

/proc/test_map_loc_write(atom/item)
    item.loc = null

/proc/test_map_coordinate_write(atom/item)
    item.x = 1

/proc/test_map_density_write(atom/item)
    item.density = 0

/proc/test_map_color_write(atom/item)
    item.color = "#000000"
