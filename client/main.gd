extends Control
## A deliberately thin presentation client. The server owns movement and world state.

const Protocol = preload("res://protocol.gd")
const BACKGROUND := Color("090e18")
const PANEL := Color("111c2c")
const MUTED := Color("90a4bc")
const TEXT := Color("e1eaf5")
const ACCENT := Color("49dcc1")
const TILE_FLOOR := Color("20394b")
const TILE_WALL := Color("50677c")

var host := "127.0.0.1"
var port := 25566
var player_name := "Explorer"
var smoke_test := false
var smoke_min_players := 1
var peer := StreamPeerTCP.new()
var incoming := PackedByteArray()
var hello_sent := false
var local_id := ""
var snapshot: Dictionary = {}
var logs: Array[String] = []
var connection_text := "Connecting"
var failed := false
var connected_at := 0
var smoke_target := Vector3i.ZERO
var smoke_started := false
var smoke_finished := false
var smoke_observed_players: Array[String] = []
var last_move_at := 0
var last_direction := Vector2i.ZERO
var title_label: Label
var subtitle_label: Label
var status_label: Label
var details_label: Label
var help_label: Label
var log_label: Label


func _ready() -> void:
	_parse_options()
	_build_labels()
	resized.connect(queue_redraw)
	connected_at = Time.get_ticks_msec()
	if port < 1 or port > 65535:
		_fail("Port must be between 1 and 65535")
		return
	var result := peer.connect_to_host(host, port)
	if result != OK:
		_fail("Cannot connect: " + error_string(result))
		return
	_add_log("Connecting to %s:%d" % [host, port])
	queue_redraw()


func _parse_options() -> void:
	var args := OS.get_cmdline_user_args()
	var i := 0
	while i < args.size():
		var arg: String = args[i]
		if arg == "--smoke-test":
			smoke_test = true
		elif arg in ["--host", "--port", "--name", "--smoke-min-players"] and i + 1 < args.size():
			i += 1
			_set_option(arg, args[i])
		elif arg.begins_with("--host=") or arg.begins_with("--port=") or arg.begins_with("--name=") or arg.begins_with("--smoke-min-players="):
			var split := arg.split("=", true, 1)
			_set_option(split[0], split[1])
		i += 1


func _set_option(key: String, value: String) -> void:
	match key:
		"--host": host = value
		"--port": port = value.to_int() if value.is_valid_int() else -1
		"--name": player_name = value.left(40)
		"--smoke-min-players": smoke_min_players = maxi(1, value.to_int())


func _process(_delta: float) -> void:
	if failed or smoke_finished:
		return
	peer.poll()
	match peer.get_status():
		StreamPeerTCP.STATUS_CONNECTED:
			if not hello_sent:
				hello_sent = true
				connection_text = "Connected · awaiting world"
				_send({"type": "hello", "name": player_name})
			_read_messages()
		StreamPeerTCP.STATUS_ERROR:
			_fail("Connection failed")
		StreamPeerTCP.STATUS_NONE:
			_fail("Server disconnected")
	if failed or smoke_finished:
		return
	if Time.get_ticks_msec() - connected_at > 15000 and (local_id.is_empty() or snapshot.is_empty() or smoke_test):
		_fail("Timed out waiting for " + ("move confirmation" if smoke_started else "world snapshot"))
		return
	if not smoke_test and not snapshot.is_empty():
		_handle_movement()
	_refresh_labels()


func _read_messages() -> void:
	var available := peer.get_available_bytes()
	if available == 0:
		return
	var read := peer.get_data(mini(available, Protocol.MAX_FRAME_BYTES + 1))
	if read[0] != OK:
		_fail("Socket read failed")
		return
	incoming.append_array(read[1])
	var newline := incoming.find(10)
	while newline >= 0:
		if newline > Protocol.MAX_FRAME_BYTES:
			_fail("Server frame exceeds limit")
			return
		var line := incoming.slice(0, newline).get_string_from_utf8()
		incoming = incoming.slice(newline + 1)
		if not line.strip_edges().is_empty():
			var decoded: Dictionary = Protocol.decode_message(line)
			if not decoded.ok:
				_fail("Protocol error: " + decoded.error)
				return
			_receive(decoded.message)
			if failed or smoke_finished:
				return
		newline = incoming.find(10)
	if incoming.size() > Protocol.MAX_FRAME_BYTES:
		_fail("Server frame exceeds limit")


func _receive(message: Dictionary) -> void:
	match message.type:
		"welcome":
			if not local_id.is_empty():
				_fail("Duplicate welcome")
				return
			local_id = str(message.id)
			_add_log("Joined as %s · player %s" % [player_name, local_id])
		"snapshot":
			if local_id.is_empty():
				_fail("Snapshot arrived before welcome")
				return
			snapshot = message
			connection_text = "Connected · authoritative simulation"
			queue_redraw()
			if smoke_test:
				_step_smoke()
		"log":
			_add_log(message.text)
		"error":
			_add_log("Server: " + message.text)
			if smoke_test:
				_fail("Server rejected operation: " + message.text)


func _send(message: Dictionary) -> void:
	var result := peer.put_data((JSON.stringify(message) + "\n").to_utf8_buffer())
	if result != OK:
		_fail("Socket write failed")


func _handle_movement() -> void:
	var direction := Vector2i.ZERO
	if Input.is_physical_key_pressed(KEY_LEFT) or Input.is_physical_key_pressed(KEY_A):
		direction = Vector2i.LEFT
	elif Input.is_physical_key_pressed(KEY_RIGHT) or Input.is_physical_key_pressed(KEY_D):
		direction = Vector2i.RIGHT
	elif Input.is_physical_key_pressed(KEY_UP) or Input.is_physical_key_pressed(KEY_W):
		direction = Vector2i(0, 1)
	elif Input.is_physical_key_pressed(KEY_DOWN) or Input.is_physical_key_pressed(KEY_S):
		direction = Vector2i(0, -1)
	var now := Time.get_ticks_msec()
	if direction != Vector2i.ZERO and (direction != last_direction or now - last_move_at >= 150):
		_send({"type": "move", "dx": direction.x, "dy": direction.y})
		last_move_at = now
	last_direction = direction


func _local_player() -> Dictionary:
	for player in snapshot.get("players", []):
		if str(player.id) == local_id:
			return player
	return {}


func _step_smoke() -> void:
	var player := _local_player()
	if player.is_empty():
		return
	var position := Vector3i(int(player.x), int(player.y), int(player.z))
	if smoke_started:
		if position == smoke_target:
			smoke_finished = true
			print("CLIENT_SMOKE_OK " + JSON.stringify({"id": local_id, "x": position.x, "y": position.y, "z": position.z, "observed_players": smoke_observed_players}))
			peer.disconnect_from_host()
			get_tree().quit(0)
		return
	if snapshot.players.size() < smoke_min_players:
		return
	for observed in snapshot.players:
		smoke_observed_players.append(str(observed.id))
	for offset in [Vector3i(1, 0, 0), Vector3i(0, 1, 0), Vector3i(-1, 0, 0), Vector3i(0, -1, 0)]:
		var candidate: Vector3i = position + offset
		var occupied := false
		for other in snapshot.players:
			if Vector3i(int(other.x), int(other.y), int(other.z)) == candidate:
				occupied = true
				break
		if occupied:
			continue
		for tile in snapshot.tiles:
			if Vector3i(int(tile.x), int(tile.y), int(tile.z)) == candidate and not tile.density:
				smoke_target = candidate
				smoke_started = true
				_send({"type": "move", "dx": offset.x, "dy": offset.y})
				return
	_fail("No adjacent passable tile for movement smoke test")


func _fail(reason: String) -> void:
	failed = true
	connection_text = "Disconnected · " + reason
	_add_log(reason)
	peer.disconnect_from_host()
	_refresh_labels()
	queue_redraw()
	if smoke_test:
		printerr("CLIENT_SMOKE_FAILED " + reason)
		get_tree().quit(1)


func _add_log(message: String) -> void:
	logs.append(message.replace("\n", " ").left(160))
	while logs.size() > 6:
		logs.pop_front()
	_refresh_labels()


func _make_label(font_size: int, color: Color) -> Label:
	var label := Label.new()
	label.add_theme_font_size_override("font_size", font_size)
	label.add_theme_color_override("font_color", color)
	label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(label)
	return label


func _build_labels() -> void:
	title_label = _make_label(28, TEXT)
	title_label.text = "DM / GODOT"
	subtitle_label = _make_label(14, MUTED)
	subtitle_label.text = "Compatibility engine · diagnostic world viewer"
	status_label = _make_label(14, ACCENT)
	details_label = _make_label(15, TEXT)
	help_label = _make_label(13, MUTED)
	help_label.text = "MOVE MARKER   Arrows / WASD\n\nOriginal DM → OpenDream bytecode\n→ independent runtime → Godot\n\nDiagnostic markers, not DM mobs\nDMI appearance renderer pending"
	log_label = _make_label(13, MUTED)
	_refresh_labels()


func _refresh_labels() -> void:
	if status_label == null:
		return
	title_label.position = Vector2(32, 24)
	subtitle_label.position = Vector2(34, 62)
	status_label.position = Vector2(34, 96)
	status_label.text = connection_text
	status_label.modulate = Color("fa8a83") if failed else Color.WHITE
	details_label.position = Vector2(34, 160)
	var player := _local_player()
	var location := "Waiting for world"
	if not player.is_empty():
		location = "Tile %d, %d · level %d" % [player.x, player.y, player.z]
	details_label.text = "%s\n%s\n\n%d connected players\n%s:%d" % [player_name, location, snapshot.get("players", []).size(), host, port]
	help_label.position = Vector2(34, 306)
	log_label.position = Vector2(34, size.y - 158)
	log_label.text = "EVENTS\n" + "\n".join(logs)


func _draw() -> void:
	draw_rect(Rect2(Vector2.ZERO, size), BACKGROUND)
	draw_rect(Rect2(16, 140, 306, maxf(320, size.y - 324)), PANEL)
	draw_line(Vector2(34, 130), Vector2(size.x - 34, 130), Color("26374b"), 1)
	draw_line(Vector2(34, size.y - 173), Vector2(size.x - 34, size.y - 173), Color("26374b"), 1)
	if snapshot.is_empty():
		return
	var board := Rect2(346, 152, maxf(100, size.x - 378), maxf(100, size.y - 348))
	var cell := minf(board.size.x / snapshot.width, board.size.y / snapshot.height)
	var origin := board.position + (board.size - Vector2(snapshot.width, snapshot.height) * cell) / 2.0
	var player := _local_player()
	var level := int(player.get("z", 1))
	for tile in snapshot.tiles:
		if int(tile.z) != level:
			continue
		var rect := Rect2(origin + Vector2(tile.x - 1, snapshot.height - tile.y) * cell, Vector2.ONE * cell)
		draw_rect(rect.grow(-1.0), TILE_WALL if tile.density else TILE_FLOOR)
		if tile.density:
			draw_line(rect.position + Vector2(3, 3), rect.position + Vector2(cell - 3, 3), Color("718b9f"), 2)
	for other in snapshot.players:
		if int(other.z) != level:
			continue
		var center := origin + Vector2(other.x - 0.5, snapshot.height - other.y + 0.5) * cell
		var is_local := str(other.id) == local_id
		var color := ACCENT if is_local else Color("e8b476")
		draw_circle(center, cell * 0.25, color)
		if is_local:
			draw_arc(center, cell * 0.34, 0, TAU, 32, color, 2, true)
		var font := ThemeDB.fallback_font
		var text_size := font.get_string_size(str(other.name), HORIZONTAL_ALIGNMENT_LEFT, -1, 12)
		draw_string(font, center + Vector2(-text_size.x / 2.0, cell * 0.43), str(other.name), HORIZONTAL_ALIGNMENT_LEFT, -1, 12, TEXT)


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST:
		peer.disconnect_from_host()
		get_tree().quit()
