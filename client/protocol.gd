extends RefCounted
## Validation for the version-1 JSON-lines presentation protocol.
## This protocol is not BYOND wire compatibility.

const VERSION := 1
const MAX_FRAME_BYTES := 4 * 1024 * 1024


static func decode_message(line: String) -> Dictionary:
	var parser := JSON.new()
	if parser.parse(line) != OK:
		return _error("Invalid JSON")
	var value: Variant = parser.data
	if not value is Dictionary or not value.get("type") is String:
		return _error("Message must have a string type")
	match value.type:
		"welcome":
			if value.get("protocol") != VERSION or not _identifier(value.get("id")):
				return _error("Unsupported protocol or invalid player ID")
		"snapshot":
			if not _positive_integer(value.get("width")) or not _positive_integer(value.get("height")):
				return _error("Snapshot dimensions must be positive integers")
			if value.width > 8192 or value.height > 8192:
				return _error("Snapshot dimensions exceed prototype limits")
			if not value.get("tiles") is Array or not value.get("players") is Array:
				return _error("Snapshot tiles and players must be arrays")
			for tile in value.tiles:
				if not tile is Dictionary or not _position(tile, value.width, value.height):
					return _error("Invalid tile coordinates")
				if not tile.get("density") is bool or not tile.get("type") is String:
					return _error("Invalid tile density or type")
			for player in value.players:
				if not player is Dictionary or not _position(player, value.width, value.height):
					return _error("Invalid player coordinates")
				if not _identifier(player.get("id")) or not player.get("name") is String:
					return _error("Invalid player identity")
		"log", "error":
			if not value.get("text") is String:
				return _error("Message text must be a string")
		_:
			return _error("Unknown server message type: " + str(value.type))
	return {"ok": true, "message": value}


static func _position(value: Dictionary, width: int, height: int) -> bool:
	return (_positive_integer(value.get("x")) and _positive_integer(value.get("y"))
		and _positive_integer(value.get("z")) and value.x <= width and value.y <= height)


static func _positive_integer(value: Variant) -> bool:
	return ((value is int or value is float) and is_finite(float(value))
		and value > 0 and value == floor(value))


static func _identifier(value: Variant) -> bool:
	return (value is String and not value.is_empty()) or _positive_integer(value)


static func _error(message: String) -> Dictionary:
	return {"ok": false, "error": message}
