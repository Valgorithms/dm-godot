// Ordinary DM sources, compiled by the pinned upstream compiler for regression tests.
/world/New()
    return

/proc/test_waitfor()
    world << "caller-start"
    detached_worker()
    world << "caller-after"

/proc/detached_worker()
    set waitfor = 0
    world << "worker-start"
    sleep(1)
    world << "worker-end"

/proc/test_spawn_copy()
    var/local_value = 1
    spawn(1)
        world << "spawn-local=[local_value]"
    local_value = 2
    world << "parent-local=[local_value]"

/proc/test_shared_list()
    var/list/values = list(1)
    spawn(1)
        world << "spawn-list=[values[1]]"
    values[1] = 2

/proc/test_zero_delay()
    spawn(0)
        world << "spawn-zero"
    world << "before-zero"
    sleep(0)
    world << "after-zero"

/proc/test_negative_spawn()
    spawn(-1)
        world << "spawn-negative"
    world << "after-negative"

/proc/test_arithmetic()
    var/value = 2
    value += 3
    if(value == 5)
        world << "arithmetic-ok"
    else
        world << "arithmetic-bad"

/proc/test_fractional_modulo_error()
    var/a = 1
    var/b = 0.5
    world << a % b

/proc/test_integer_modulo()
    var/a = 5.9
    var/b = 2.1
    world << "mod=[a % b]"
    a = -5.9
    world << "mod=[a % b]"
    a = -2147483648
    b = -1
    world << "mod=[a % b]"

/proc/test_negative_spawn_recursion()
    spawn(-1)
        test_negative_spawn_recursion()

/proc/test_world_static()
    world << "time=[world.time], fps=[world.fps]"

/proc/test_world_tick_usage()
    world << world.tick_usage

/proc/test_world_contents()
    world << world.contents

/proc/test_world_realtime()
    world << world.realtime
