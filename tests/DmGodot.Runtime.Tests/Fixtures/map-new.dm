world
    area = /area/region/unused
    turf = /turf/tile

var/initialized = initialize_globals()
var/last_new = 0
var/last_turf_new = 0
var/unused_x
var/unused_y
var/unused_z
var/area/unused_area

/proc/initialize_globals()
    world << "global"
    return 1

/area/region
    var/label = "unused"
    var/new_count = 0
    New()
        ..()
        new_count += 1
        world << "area-[label]:[initialized]:[loc == null]"
        if(label == "unused")
            unused_area = src
            unused_x = x
            unused_y = y
            unused_z = z
        spawn(0)
            world << "spawn-area-[label]"
        sleep(1)
        world << "wake-area-[label]"

/area/region/a
    label = "a"

/area/region/b
    label = "b"

/area/region/unused

/turf/tile
    var/new_count = 0
    var/new_order = 0
    New()
        ..()
        new_count += 1
        last_turf_new += 1
        new_order = last_turf_new
        world << "turf-[x],[y]"
        spawn(0)
            world << "spawn-turf-[x],[y]"
        sleep(1)
        world << "wake-turf-[x],[y]"

/obj/marker
    var/new_count = 0
    var/forwarding_count = 0
    var/new_order = 0
    New(location)
        ..()
        new_count += 1
        last_new += 1
        new_order = last_new
        world << "obj-[x],[y]:[location == loc]"
        spawn(0)
            world << "spawn-obj-[x],[y]"
        sleep(1)
        world << "wake-obj-[x],[y]"

/obj/marker/child
    New(location)
        forwarding_count += 1
        ..(location)
        forwarding_count += 1

/obj/marker/child/grandchild
    New(location)
        forwarding_count += 1
        ..(location)
        forwarding_count += 1

/world/New()
    world << "world"
    spawn(0)
        world << "spawn-world"
    sleep(1)
    world << "wake-world"
