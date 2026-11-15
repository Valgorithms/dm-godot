var/global/result
var/global/key_one
var/global/key_two
var/global/strict_one
var/global/strict_two

world
    area = /area/plain

/area/plain
/turf/plain

/obj/holder
    var/alist/items = alist(0 = list(1), 2.9 = 3, (null) = 4)
    var/alist/empty = alist()
    var/new_count = 0
    New()
        new_count += 1

/obj/holder/inherited
/obj/holder/replaced
    items = alist(0 = list(9))
/obj/holder/nulled
    items = null

/proc/key_literals()
    result = alist(0 = "zero", -1 = "negative", 2.9 = "fraction", (null) = "true-null", null = "named-null", "empty", 0 = "updated")

/proc/numeric_keys(a, b, c)
    result = alist((a) = 1, (b) = 2, (c) = 3)

/proc/exact_index(values, key)
    result = values[key]

/proc/index_update(values, key, value)
    values[key] = value
    result = values[key]

/proc/compound_index(values, key)
    values[key] += 1
    result = values[key]

/proc/identity_keys()
    key_one = list(1)
    key_two = list(1)
    strict_one = alist(0 = 1)
    strict_two = alist(0 = 1)
    result = alist((key_one) = 1, (key_two) = 2, (strict_one) = 3, (strict_two) = 4)

/proc/object_keys()
    key_one = new /datum
    key_two = new /datum
    result = alist((key_one) = 1, (key_two) = 2)

/proc/value_properties()
    var/alist/a = alist()
    var/alist/b = alist()
    result = list(a == a, a == b, !!a, length(a), a.len)

/proc/lengths(values)
    result = list(length(values), values.len)

/proc/type_guard()
    var/alist/a = alist()
    result = a.type
/proc/islist_guard()
    result = islist(alist())
/proc/istype_guard()
    result = istype(alist(), /alist)
/proc/string_guard()
    world << "[alist()]"
/proc/output_guard()
    world << alist()
/proc/operator_guard()
    result = alist() + 1
/proc/enumeration_guard()
    for (var/key in alist(0 = 1))
        result = key
/proc/native_guard()
    var/alist/a = alist()
    result = a.Copy()
/proc/new_guard()
    result = new /alist
/proc/len_write_guard()
    var/alist/a = alist(0 = 1)
    a.len = 0
/proc/type_write_guard()
    var/alist/a = alist()
    a.type = /list

/proc/growth_budget()
    result = alist()
    for (var/i = 1; i <= 50001; i++)
        result[i] = i

/proc/overwrite_budget()
    result = alist(0 = 1)
    for (var/i = 1; i <= 60000; i++)
        result[0] = i

/proc/probe()
    result = null

/obj/unsafe_alist_helper
    var/alist/items = alist("key" = sleep(0))
/obj/unsafe_alist_read
    var/alist/items = alist("key" = world.time)
/obj/unsafe_alist_native
    contents = alist()
