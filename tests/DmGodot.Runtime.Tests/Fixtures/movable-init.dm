var/global/list/result

world
    area = /area/plain
    New()
        world << "world"

/area/plain
    New()
        world << "area"

/turf/plain
    New()
        world << "turf"

/obj/local
    var/list/items = list(1, list(2))
    var/list/empty = list()
    var/list/keyed = list("a" = list(3), list("key") = /area/plain, "a" = list(4))
    var/sized[2][3]
    var/new_count = 0
    var/loc_seen
    var/usr_seen
    New(where)
        new_count += 1
        loc_seen = (loc == where)
        usr_seen = (usr == null)
        world << "obj=[items[1]]"
        sleep(1)
        world << "awake=[items[1]]"

/obj/local/inherited
/obj/local/replaced
    items = list(9)
/obj/local/nulled
    items = null
    New(where)
        new_count += 1
        loc_seen = (loc == where)
        world << "nulled"

/proc/effectful_helper()
    world << "forbidden-effect"
    sleep(0)
    return 7

/obj/unsafe_helper
    var/list/items = list(effectful_helper())
/obj/unsafe_sleep
    var/list/items = list(sleep(0))
/obj/unsafe_read
    var/list/items = list(world.time)
/obj/unsafe_native
    contents = list()
/obj/unsafe_new
    var/value = new /datum
/obj/local/unsafe_child
    var/list/other = list(effectful_helper())
/obj/unsafe_alist
    var/list/items = alist(1 = list())

/proc/literal_lists()
    result = list("a" = 1, "plain", "a" = 2, (null) = "null-value", 1 = "replaced", 4 = "grown", "plain")

/proc/fractional_list_keys()
    result = list("first", 1.9 = "changed", 2.9 = "second")

/proc/dimension_sizes(a, b)
    var/list/result[a][b]
    global.result = result

/proc/single_dimension(a)
    var/list/result[a]
    global.result = result

/proc/assoc_zero()
    result = list(0 = 1)
/proc/assoc_gap()
    result = list(2 = 1)
/proc/assoc_number(key)
    result = list((key) = 1)

/proc/aggregate_lists()
    var/list/a[60000]
    var/list/b[60000]
    result = list(a, b)

/proc/boot_allocation_budget()
    var/list/first[60000]
    spawn(0)
        var/list/second[60000]
        result = second
    result = first

/proc/bare_null_key()
    result = list(null = "named-null")
