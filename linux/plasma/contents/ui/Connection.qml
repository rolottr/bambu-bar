import QtQuick
import org.kde.plasma.workspace.dbus as DBus

// One initial read, then signals only. No process, timer or second printer connection.
Item {
    id: root
    visible: false
    property var snapshot: ({})
    readonly property string service: "dev.rolottr.BambuBar.Desktop"
    readonly property string path: "/dev/rolottr/BambuBar/Desktop"
    readonly property bool connected: watcher.registered
    function accept(json) {
        try {
            const value = JSON.parse(json);
            snapshot = value.version === 1 ? value : {};
        } catch (error) { snapshot = {}; }
    }
    function read() {
        DBus.SessionBus.asyncCall({service, path, iface: service, member: "GetState"},
            reply => root.accept(reply.value), () => { root.snapshot = {}; });
    }
    function invoke(action, printer = snapshot.printer?.id || "") {
        const page = ({show:"Printer", settings:"Settings", filament:"Filament", fleet:"All Printers", widgets:"Widgets", license:"About"})[action]
        function openPage() {
            DBus.SessionBus.asyncCall({service:"dev.rolottr.BambuBar.Native", path:"/dev/rolottr/BambuBar/Native", iface:"dev.rolottr.BambuBar.Native", member:"Open", arguments:[page]}, () => {}, () => {})
        }
        if (page && !connected) { openPage(); return }
        DBus.SessionBus.asyncCall({service, path, iface:service, member:"Invoke", arguments:[action, printer]},
            () => {}, error => { if (page) openPage(); else console.warn("Bambu Bar action failed:", error.error.message) })
    }
    function setOpen(open) {
        DBus.SessionBus.asyncCall({service, path, iface:service, member:"SetOpen", arguments:[open]}, () => {}, () => {});
    }
    function openPrinter(serial) {
        invoke("show", serial)
    }
    DBus.DBusServiceWatcher {
        id: watcher
        busType: DBus.BusType.Session
        watchedService: root.service
        onRegisteredChanged: { if (registered) root.read(); else root.snapshot = {}; }
    }
    DBus.SignalWatcher {
        busType: DBus.BusType.Session
        service: root.service
        path: root.path
        iface: root.service
        function dbusStateChanged(json) { root.accept(json); }
    }
    Component.onCompleted: { if (connected) read(); }
}
