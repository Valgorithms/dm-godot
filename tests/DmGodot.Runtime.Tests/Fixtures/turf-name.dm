/turf/base
    name = "type-name"
    var/marker = 3

/turf/named
    parent_type = /turf/base
    New()
        world << "[x]:[name]:[marker]"

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
