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
    world << value:plane
/proc/read_turf_state()
    var/turf/t = locate(1,1,1)
    world << t.icon_state


var/entity_initial_result
/proc/test_entity_initial(value)
    entity_initial_result = initial(value:scalar)

/obj/icon_sign
    var/icon_seen
    var/matched_literal
    var/state_seen
    New()
        icon_seen = icon
        matched_literal = (icon == 'icons/sign.dmi')
        state_seen = icon_state

/obj/icon_sign/with_default
    icon = 'icons/sign.dmi'

/obj/icon_sign/with_default/child

/proc/write_sign_icon(obj/sign)
    sign.icon = null

/proc/sign_icon_text(obj/sign)
    world << "[sign.icon]"

/proc/sign_file_query()
    return fexists("icons/sign.dmi")

/obj/visibility_sign
    invisibility = 7
    var/seen_at_new
    New()
        seen_at_new = invisibility

/obj/visibility_sign/child

var/global/observed_invisibility
/proc/read_invisibility(value)
    observed_invisibility = value:invisibility
/proc/write_invisibility(value)
    value:invisibility = 0

/obj/described_item
    desc = "inherited description"
    var/description_at_new = "before New"
    New()
        description_at_new = desc

/obj/described_item/child

var/global/observed_description
/proc/read_description(value)
    observed_description = value:desc
/proc/write_description(value)
    value:desc = "changed"

/obj/direction_marker
    var/direction_at_new
    var/where_at_new
    New(where)
        direction_at_new = dir
        where_at_new = (loc == where)

/obj/direction_marker/angled
    dir = NORTHWEST

/obj/direction_marker/angled/child

var/global/observed_direction
/proc/read_direction(value)
    observed_direction = value:dir
/proc/write_direction(value)
    value:dir = WEST
/proc/initial_direction(value)
    observed_direction = initial(value:dir)

/obj/offset_marker
    var/birth_px
    var/birth_py
    var/birth_x
    var/birth_y
    var/birth_z
    var/birth_turf
    var/where_matches
    New(where)
        birth_px = pixel_x
        birth_py = pixel_y
        birth_x = x
        birth_y = y
        birth_z = z
        birth_turf = loc
        where_matches = (loc == where)

/obj/offset_marker/shifted
    pixel_x = 13.75
    pixel_y = -11.25

/obj/offset_marker/shifted/child

var/global/observed_px
var/global/observed_py
/proc/read_offsets(value)
    observed_px = value:pixel_x
    observed_py = value:pixel_y
/proc/write_offset_x(value)
    value:pixel_x = 7
/proc/write_offset_y(value)
    value:pixel_y = 7
/proc/read_offset_y(value)
    observed_py = value:pixel_y
/proc/initial_offset(value)
    observed_px = initial(value:pixel_x)
