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

/proc/test_literal_lists()
    var/list/numbers = literal_numbers()
    var/list/strings = literal_strings()
    var/list/resources = literal_resources()
    world << "numbers=[numbers.len]:[numbers[1]],[numbers[2]],[numbers[3]]"
    world << "strings=[strings.len]:[strings[1]],[strings[2]],[strings[3]]"
    world << "resources=[resources.len]:[resources[1]],[resources[2]]"

/proc/test_reference_lists()
    var/list/shared = list(1)
    var/list/values = literal_refs(shared, shared)
    shared[1] = 7
    var/list/first = values[1]
    var/list/second = values[2]
    world << "references=[values.len]:[first[1]],[second[1]]"
    first[1] = 9
    world << "shared=[shared[1]],[second[1]]"

/proc/literal_numbers()
    return list(3, 1, 3)

/proc/literal_strings()
    return list("third", "first", "third")

/proc/literal_resources()
    return list('semantics.dm', 'semantics.dme')

/proc/literal_refs(a, b)
    return list(a, b)

var/init_count = 0
/proc/count_initializer()
    init_count += 1
    return init_count

/datum/init_marker
    var/value
    New()
        value = count_initializer()

/datum/construction_parent
    var/datum/init_marker/parent_value = new /datum/init_marker
    var/list/values = list(1)
    var/argument_value = 0
    New(value)
        argument_value = value
        return 99

/datum/construction_parent/child
    var/datum/init_marker/child_value = new /datum/init_marker
    New(value)
        ..(value + 1)
        return 123

/proc/test_datum_initialization()
    var/datum/construction_parent/child/first = new(4)
    var/datum/construction_parent/child/second = new(8)
    first.values[1] = 7
    world << "init=[first.parent_value.value],[first.child_value.value],[second.parent_value.value],[second.child_value.value]"
    world << "args=[first.argument_value],[second.argument_value]"
    world << "lists=[first.values[1]],[second.values[1]]"
    world << "distinct=[first != second]"

/datum/sleeping_constructor
    var/value = 0
    New(argument)
        world << "new-start"
        sleep(1)
        value = argument
        world << "new-end"
        return 99

/proc/test_sleeping_constructor()
    world << "caller-start"
    var/datum/sleeping_constructor/result = new(7)
    world << "caller-result=[result.value]"

/datum/detached_constructor
    var/value = 1
    New()
        set waitfor = 0
        world << "detached-start"
        sleep(1)
        value = 2
        world << "detached-end"

/proc/test_detached_constructor()
    var/datum/detached_constructor/result = new
    world << "detached-result=[result.value]"
    sleep(1)
    world << "detached-later=[result.value]"

/datum/nested_constructor
    var/datum/sleeping_constructor/child
    New()
        child = new /datum/sleeping_constructor(11)

/proc/test_nested_constructor()
    var/datum/nested_constructor/result = new
    world << "nested-result=[result.child.value]"

/proc/test_dynamic_datum()
    var/path = /datum/construction_parent
    var/datum/construction_parent/result = new path(6)
    world << "dynamic=[result.argument_value]"

/proc/test_native_creation_error()
    var/obj/result = new /obj
    return result

/proc/test_plain_datum()
    var/datum/result = new /datum
    world << "plain=[result.type == /datum]"

/datum/recursive_constructor
    New()
        new /datum/recursive_constructor

/proc/test_constructor_depth()
    new /datum/recursive_constructor

/proc/test_native_datum_error()
    new /exception

/proc/test_datum_parent_type()
    var/datum/construction_parent/child/result = new(0)
    world << "parent=[result.parent_type == /datum/construction_parent]"

/proc/test_datum_vars_error()
    var/datum/result = new /datum
    world << result.vars

/proc/test_datum_parent_write_error()
    var/datum/result = new /datum
    result.parent_type = /datum

/datum/construction_parent/inherited_only

/proc/test_inherited_initializer()
    var/datum/construction_parent/inherited_only/result = new(3)
    world << "inherited=[result.parent_value.value],[result.argument_value]"

/datum/detached_nested_constructor
    var/datum/sleeping_constructor/child
    New()
        set waitfor = 0
        child = new /datum/sleeping_constructor(12)
        world << "outer-done"

/proc/test_detached_nested_constructor()
    var/datum/detached_nested_constructor/result = new
    world << "outer-returned=[result.child == null]"
    sleep(1)
    world << "outer-later=[result.child.value]"

/datum/method_parent
    var/value = 0
    proc/compute(a, b)
        value = a * 10 + b
        return value
    proc/identity()
        return src
    proc/user_context()
        return usr
    proc/relay(datum/method_parent/target)
        return target.compute(2, 3)
    proc/sleeping(a)
        world << "method-start=[value]"
        sleep(1)
        value = a
        world << "method-end=[value]"
        return value
    proc/detached(a)
        set waitfor = 0
        world << "method-detached-start"
        sleep(1)
        value = a
        world << "method-detached-end=[value]"

/datum/method_parent/child
    compute(a, b)
        return ..() + 100

/proc/test_instance_dispatch()
    var/datum/method_parent/first = new /datum/method_parent/child
    var/datum/method_parent/second = new
    world << "override=[first.compute(4, 5)]"
    world << "relay=[first.relay(second)]"
    world << "receivers=[first.value],[second.value]"
    world << "identity=[first.identity() == first],[second.identity() == second]"
    world << "usr=[first.user_context() == null]"

/proc/test_sleeping_method()
    var/datum/method_parent/target = new
    world << "method-result=[target.sleeping(6)]"
    world << "method-caller=[target.value]"

/proc/test_detached_method()
    var/datum/method_parent/target = new
    target.detached(9)
    world << "method-caller=[target.value]"
    sleep(1)
    world << "method-later=[target.value]"

/proc/test_null_method()
    var/datum/method_parent/target
    target.compute(1, 2)

/proc/test_missing_method()
    var/datum/target = new
    target:missing_method()

/proc/test_native_method()
    world.Profile()

/datum/method_recursive
    proc/recurse()
        return src.recurse()

/proc/test_method_depth()
    var/datum/method_recursive/target = new
    target.recurse()

/proc/test_nonobject_method()
    var/target = 7
    target:compute(1, 2)

/proc/test_named_method_error()
    var/datum/method_parent/target = new
    target.compute(b = 2, a = 1)
