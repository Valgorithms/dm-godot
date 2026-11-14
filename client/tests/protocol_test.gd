extends SceneTree

const Protocol = preload("res://protocol.gd")
var passed := 0
var failures: Array[String] = []


func _initialize() -> void:
	_check("welcome", '{"type":"welcome","id":"1","protocol":1}', true)
	_check("numeric ID", '{"type":"welcome","id":2,"protocol":1}', true)
	_check("version mismatch", '{"type":"welcome","id":"1","protocol":2}', false)
	_check("invalid JSON", '{', false)
	_check("non-object", '[]', false)
	_check("unknown message", '{"type":"magic"}', false)
	_check("log", '{"type":"log","text":"world.New ran"}', true)
	_check("invalid log", '{"type":"log","text":null}', false)
	var valid := {"type": "snapshot", "width": 3, "height": 3,
		"tiles": [{"x": 1, "y": 2, "z": 1, "density": false, "type": "/turf/floor"}],
		"players": [{"id": "1", "name": "A", "x": 1, "y": 2, "z": 1}]}
	_check("snapshot", JSON.stringify(valid), true)
	var invalid: Dictionary = valid.duplicate(true)
	invalid.tiles[0].x = 0
	_check("zero coordinate", JSON.stringify(invalid), false)
	invalid = valid.duplicate(true)
	invalid.tiles[0].x = 1.5
	_check("fractional coordinate", JSON.stringify(invalid), false)
	invalid = valid.duplicate(true)
	invalid.players[0].y = 4
	_check("out of map player", JSON.stringify(invalid), false)
	invalid = valid.duplicate(true)
	invalid.tiles[0].density = "false"
	_check("non-boolean density", JSON.stringify(invalid), false)
	invalid = valid.duplicate(true)
	invalid.width = 9000
	_check("excessive dimensions", JSON.stringify(invalid), false)
	if failures.is_empty():
		print("PROTOCOL_TESTS_OK %d cases" % passed)
		quit(0)
	else:
		for failure in failures:
			printerr(failure)
		quit(1)


func _check(name: String, line: String, expected: bool) -> void:
	var result: Dictionary = Protocol.decode_message(line)
	if result.ok == expected:
		passed += 1
	else:
		failures.append("FAIL " + name + ": " + JSON.stringify(result))
