import QtQuick

// A single-line text field in the settings style: placeholder, optional secret echo, commit on Return or when focus
// leaves, Esc restores the committed text.
Item {
    id: field
    property string text
    property string placeholder
    property bool secret: false
    property bool monospace: false
    property bool error: false
    property string accessibleName
    signal edited(string text)
    signal committed(string text)
    implicitWidth: 240
    implicitHeight: 30
    Accessible.role: Accessible.EditableText
    Accessible.name: accessibleName

    function restore() { input.text = field.text }
    onTextChanged: if (!input.activeFocus) input.text = field.text

    Rectangle {
        anchors.fill: parent
        radius: 7
        color: Theme.field
        border.width: input.activeFocus ? 1.5 : field.error ? 1 : 0
        border.color: field.error ? Theme.danger : Theme.accent
        Behavior on border.color { ColorAnimation { duration: 120 } }
    }
    TextInput {
        id: input
        anchors.fill: parent
        anchors.leftMargin: 10
        anchors.rightMargin: 10
        verticalAlignment: TextInput.AlignVCenter
        text: field.text
        color: Theme.ink
        selectionColor: Theme.accent
        selectedTextColor: "white"
        font.pixelSize: 13
        font.family: field.monospace ? "monospace" : Qt.application.font.family
        clip: true
        selectByMouse: true
        echoMode: field.secret ? TextInput.Password : TextInput.Normal
        activeFocusOnTab: true
        Accessible.name: field.accessibleName
        onTextEdited: field.edited(text)
        onAccepted: field.committed(text)
        onActiveFocusChanged: if (!activeFocus && text !== field.text) field.committed(text)
        Keys.onEscapePressed: {
            text = field.text
            focus = false
        }
    }
    Text {
        anchors.left: input.left
        anchors.verticalCenter: parent.verticalCenter
        visible: input.text.length === 0 && !input.activeFocus
        text: field.placeholder
        color: Theme.ink3
        font.pixelSize: 13
    }
}
