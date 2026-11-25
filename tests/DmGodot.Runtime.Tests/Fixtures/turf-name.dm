/turf/base
    name = "type-name"
    layer = 2.5
    var/layer_seen
    var/marker = 3

/turf/named
    parent_type = /turf/base
    New()
        world << "[x]:[name]:[marker]"
        layer_seen = layer

/proc/test_turf_override_isolation()
    var/turf/base/a = locate(1,1,1)
    var/turf/base/b = locate(2,1,1)
    a.marker = 99
    world << "markers=[a.marker],[b.marker]"

/proc/test_turf_name_write()
    var/turf/base/a = locate(1,1,1)
    a.name = "changed"

/proc/test_turf_name_output()
    var/turf/base/a = locate(1,1,1)
    world << a

var/global/materials = 13

/turf/initialized_base
    parent_type = /turf/base
    var/list/state = list(1, list(2))
    var/choice = list(3)
    var/list/materials = list(/turf/base = 50)
    New()
        world << "init=[x]:[marker]:[state[2][1]]:[choice[1]]"

/turf/initialized_child
    parent_type = /turf/initialized_base

/turf/initialized_child/replaced
    choice = list(4)

/turf/initialized_child/identity_matrix
    transform = matrix()

/turf/initialized_child/translated_matrix
    transform = matrix(1, 0, -4, 0, 1, -4)

/turf/initialized_child/ordinary_key
    var/key = list(5)

/turf/initialized_child/dimensional
    var/dimensions[2][3]

/turf/initialized_child/multiple_keys
    var/value = list(/turf = 1, /turf/base = 2)

/turf/initialized_child/text_key
    var/value = list("key" = 3)

/proc/effectful_turf_helper()
    world << "unexpected initializer effect"
    return 7

/turf/initialized_child/unsafe_helper
    var/value = list(effectful_turf_helper())

/turf/initialized_child/unsafe_sleep
    var/value = list(sleep(0))

var/global/turf_init_sentinel

/turf/initialized_child/unsafe_global
    var/value = list((global.turf_init_sentinel = 1))

/turf/initialized_child/unsafe_native
    contents = list()

/proc/test_turf_layer_write()
    var/turf/base/a = locate(1,1,1)
    a.layer = 4

/proc/test_area_layer_read()
    var/turf/a = locate(1,1,1)
    var/area/region = a.loc
    world << region.layer

/proc/test_movable_layer_read(mob/item)
    world << item.layer
