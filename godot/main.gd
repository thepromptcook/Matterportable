# Starts the C# launcher, or explains what's missing when this editor can't run C#.
extends Node

func _ready() -> void:
	if not ClassDB.class_exists("CSharpScript"):
		var label := Label.new()
		label.text = "Matterportable needs the .NET build of Godot 4.4+ (download the \".NET\" version from godotengine.org)\nand the .NET 8 SDK (dotnet.microsoft.com/download). This editor is the standard build, which can't run C#."
		label.set_anchors_preset(Control.PRESET_CENTER)
		label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		add_child(label)
		push_error(label.text)
		return
	var launcher := Node.new()
	launcher.name = "Launcher"
	launcher.set_script(load("res://scripts/Launcher.cs"))
	add_child(launcher)
