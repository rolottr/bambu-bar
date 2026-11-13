import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as QQC2
import org.kde.kirigami as Kirigami
Kirigami.FormLayout {
    id: root
    property alias cfg_size: size.currentIndex
    property string cfg_printer
    Connection { id: connection }
    QQC2.ComboBox {
        id: size
        Kirigami.FormData.label: i18n("Size:")
        model: [i18n("Small"), i18n("Medium"), i18n("Large")]
    }
    QQC2.ComboBox {
        Kirigami.FormData.label: i18n("Printer:")
        model: [{id: "", name: i18n("Automatic")}, ...(connection.snapshot.printers || [])]
        textRole: "name"
        currentIndex: Math.max(0, model.findIndex(p => p.id === root.cfg_printer))
        onActivated: root.cfg_printer = model[currentIndex].id
    }
    QQC2.Label {
        text: i18n("Enable desktop widgets in Bambu Bar and keep the app running. Automatic follows the active printer.")
        wrapMode: Text.WordWrap
        Layout.maximumWidth: 360
    }
}
