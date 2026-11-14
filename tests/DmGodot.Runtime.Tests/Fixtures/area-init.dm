world
    area = /area/local

/area/local
    var/list/items = list(1, list(2))
    var/list/empty = list()
    var/new_count = 0
    New()
        new_count += 1

/area/local/inherited

/area/local/inherited2

/area/local/replaced
    items = list(3)

/area/local/nulled
    items = null

/area/local/constant
    items = 7

/proc/check_area_lists()
    var/turf/ta = locate(1,1,1)
    var/turf/tb = locate(3,1,1)
    var/turf/tc = locate(4,1,1)
    var/turf/td = locate(5,1,1)
    var/area/local/a = ta.loc
    var/area/local/b = tb.loc
    var/area/local/c = tc.loc
    var/area/local/d = td.loc
    world << "initial=[a.items[1]],[b.items[1]],[c.items == null],[d.items]"
    a.items[1] = 9
    world << "isolated=[a.items[1]],[b.items[1]]"
    world << "new=[a.new_count],[b.new_count],[c.new_count],[d.new_count]"

/proc/effectful_helper()
    world << "forbidden-effect"
    sleep(0)
    return list(9)

/area/unsafe_helper
    var/list/items = list(effectful_helper())

/area/unsafe_native
    contents = list()

/area/unsafe_read
    var/value = list(world.time)

/area/unsafe_new
    var/value = new /datum

/area/local/unsafe_child
    var/value = list(effectful_helper())

/area/unsafe_sleep
    var/list/items = list(sleep(0))
