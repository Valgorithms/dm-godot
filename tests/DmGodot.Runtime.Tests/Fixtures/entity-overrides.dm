world
    area = /area/plain

/area/plain
/turf/plain

/obj/base
    name = "inherited name"
    icon_state = "base-state"
    var/scalar = 1
    var/list/items = list(2, list(3))
    var/alist/strict = alist(0 = list(4))
    var/const/fixed = 7
    var/static/only_global = 8
    var/name_seen
    var/state_seen
    var/scalar_seen
    var/item_seen
    var/strict_seen
    var/loc_seen
    New(where)
        name_seen = name
        state_seen = icon_state
        scalar_seen = scalar
        item_seen = items[1]
        strict_seen = strict[0][1]
        loc_seen = (loc == where)

/obj/base/child
/obj/base/child/replaced
    items = list(9)

/obj/null_name
    name = null
    var/name_seen
    var/state_seen
    New()
        name_seen = name
        state_seen = icon_state

/obj/null_name/leaf_thing
/obj/base/child/reset_name
    name = null

/obj/bad_init
    name = list("forbidden")
/obj/bad_state_init
    icon_state = list("forbidden")

/proc/write_name(value)
    value:name = "changed"
/proc/write_state(value)
    value:icon_state = "changed"
/proc/output_object(value)
    world << value
/proc/interpolate_object(value)
    world << "[value]"
/proc/read_other_native(value)
    world << value:dir
/proc/read_turf_state()
    var/turf/t = locate(1,1,1)
    world << t.icon_state


var/entity_initial_result
/proc/test_entity_initial(value)
    entity_initial_result = initial(value:scalar)
