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

/turf/initialized_base
    var/list/state = list()

/turf/initialized_child
    parent_type = /turf/initialized_base

/proc/test_turf_layer_write()
    var/turf/base/a = locate(1,1,1)
    a.layer = 4

/proc/test_area_layer_read()
    var/turf/a = locate(1,1,1)
    var/area/region = a.loc
    world << region.layer

/proc/test_movable_layer_read(mob/item)
    world << item.layer
