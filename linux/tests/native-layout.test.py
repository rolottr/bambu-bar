"""Exercise live fleet layout, window resizing and menu bar settings on Plasma.

Run with the same PyQt6 and Kirigami packages as the Plasma host.
"""
import json
import os
from pathlib import Path
import unittest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
os.environ.setdefault("QT_QUICK_BACKEND", "software")

from PyQt6.QtCore import QObject, QPointF, QUrl
from PyQt6.QtQml import QQmlApplicationEngine
from PyQt6.QtQuick import QQuickWindow
from PyQt6.QtTest import QTest
from PyQt6.QtWidgets import QApplication

ROOT = Path(__file__).resolve().parents[1]


def visual_items(parent):
    for item in parent.childItems():
        yield item
        yield from visual_items(item)


class NativeLayout(unittest.TestCase):
    def setUp(self):
        self.app = QApplication.instance() or QApplication([])
        self.engine = engine = QQmlApplicationEngine()
        self.errors = errors = []
        engine.warnings.connect(lambda messages: errors.extend(m.toString() for m in messages))
        engine.loadData(b'''
            import QtQuick
            import QtQuick.Window
            import "../native/kde" as Kde
            import "../native/common" as Common
            Window {
                visible: true
                width: 1040
                height: 760
                Kde.Kit { id: nativeKit }
                Common.Content {
                    objectName: "content"
                    anchors.fill: parent
                    kit: nativeKit
                    page: "All Printers"
                }
                Common.WidgetCard {
                    objectName: "widgetCard"; kit: nativeKit
                    visible: false; size: "medium"; snapshot: null
                }
                Common.QuickView {
                    objectName: "quickView"
                    kit: nativeKit
                    controller: QtObject {
                        function t(key) { return key }
                        function activate(field) { lastAction = field.key }
                        property string lastAction: ""
                    }
                    snapshot: ({})
                    visible: false
                    width: parent.width
                }
            }
        ''',
                        QUrl.fromLocalFile(str(ROOT / 'tests/LayoutHarness.qml')))
        self.assertTrue(engine.rootObjects(), errors)
        self.window = window = engine.rootObjects()[0]
        self.assertIsInstance(window, QQuickWindow)
        self.content = window.findChild(QObject, "content")

    def tearDown(self):
        self.assertFalse(self.errors, self.errors)
        self.window.close()
        self.app.processEvents()

    def test_cards_remain_readable_and_selectable_after_resize(self):
        content = self.content
        window = self.window
        fleet = [dict(serial=str(i), name=f"Printer {i}", caption="Printing", headline="Test print",
                      progress=50, stateLine="20m left") for i in range(17)]
        state = dict(type="state", page="All Printers", pages=[], sections=[], desktop={"fleet": []})
        content.accept(json.dumps(state))
        QTest.qWait(30)
        state["desktop"]["fleet"] = fleet
        content.accept(json.dumps(state))

        for width, expected_columns in ((1040, 2), (720, 1), (1040, 2)):
            window.setWidth(width)
            QTest.qWait(50)
            visible_cards = [item for item in visual_items(window.contentItem())
                             if item.property("modelData") and item.metaObject().indexOfSignal(b"clicked()") >= 0]
            self.assertEqual(len(visible_cards), len(fleet))
            self.assertEqual(len({round(card.x()) for card in visible_cards}), expected_columns)
            self.assertTrue(all(card.width() >= 250 for card in visible_cards))
        requests = []
        content.request.connect(lambda message: requests.append(message.toVariant()))
        visible_cards[-1].clicked.emit()
        self.assertEqual([(r["action"], r["value"]) for r in requests],
                         [("select", "16"), ("navigate:Printer", "")])

    def test_printer_settings_follow_the_selected_printer(self):
        # This test exercises the form without loading a packaged 3D printer model.
        self.window.showMinimized()
        printers = [dict(id="a", name="Workshop", state="Paused"), dict(id="b", name="Lab", state="Printing")]
        for selected in printers:
            state = dict(page="Printer", type="state", pages=[],
                         desktop=dict(printers=printers, printer=selected,
                                      fleet=[dict(serial=p["id"], name=p["name"]) for p in printers]),
                         sections=[dict(title="Settings", fields=[
                             dict(key="name", label="Name", kind="input", value=selected["name"])])])
            self.content.accept(json.dumps(state))
            QTest.qWait(40)
            visible = [item for item in visual_items(self.content) if item.isVisible()]
            other_name = next(p["name"] for p in printers if p != selected)
            self.assertFalse(any(item.property("text") == other_name for item in visible))
            self.assertTrue(any(item.property("text") == selected["name"]
                                and item.property("maximumLength") == 256 for item in visible))
            buttons = [item for item in visible if item.property("text") == "Show"
                       and item.metaObject().indexOfSignal(b"clicked()") >= 0]
            self.assertFalse(buttons)

    def test_activity_page_lists_events_and_sends_its_requests(self):
        activity = dict(
            filter="", canClear=True, more=True,
            filters=[dict(value="", label="All"), dict(value="connection", label="Connection"), dict(value="issue", label="Issues")],
            capabilities=[dict(name="Workshop", model="P2S", serial="22E…", online=True, status="Online",
                               items=[dict(label="AMS", on=True, detail="1"), dict(label="Camera", on=False, detail="Off")])],
            events=[dict(id="1", category="action", icon="media-playback-start", printer="", summary="Pause print",
                         outcome="failed", outcomeLabel="Failed", time="10:41:02"),
                    dict(id="2", category="connection", icon="network-wireless", printer="", summary="Connected",
                         outcome="", outcomeLabel="", time="10:40:00")],
            labels=dict(capabilities="Reported Capabilities", noPrinter="No printer configured", events="Events",
                        empty="No activity recorded yet.", more="Show more", clear="Clear", export="Export…"))
        self.content.accept(json.dumps(dict(type="state", page="Activity", pages=[dict(id="Activity", title="Activity")],
                                            sections=[], desktop={}, activity=activity)))
        QTest.qWait(60)
        if os.environ.get("BAMBUBAR_ACTIVITY_SHOT"):
            self.window.grabWindow().save(os.environ["BAMBUBAR_ACTIVITY_SHOT"])
        texts = {item.property("text") for item in visual_items(self.content) if item.isVisible()}
        for text in ("Reported Capabilities", "Workshop", "Pause print", "Failed", "10:41:02", "Connected", "Show more"):
            self.assertIn(text, texts)
        requests, exports = [], []
        self.content.request.connect(lambda message: requests.append(message.toVariant()["action"]))
        self.content.exportRequested.connect(lambda: exports.append(True))
        def button(text):
            return next(item for item in visual_items(self.content) if item.isVisible()
                        and item.property("text") == text and item.metaObject().indexOfSignal(b"clicked()") >= 0)
        for text in ("Issues", "Show more", "Clear", "Export…"):
            button(text).clicked.emit()
        self.assertEqual(requests, ["activity-filter:issue", "activity-more", "activity-clear"])
        self.assertEqual(exports, [True])
        activity["events"] = []
        self.content.accept(json.dumps(dict(type="state", page="Activity", pages=[], sections=[], desktop={}, activity=activity)))
        QTest.qWait(30)
        self.assertIn("No activity recorded yet.", {item.property("text") for item in visual_items(self.content) if item.isVisible()})

    def test_add_widget_sends_the_selected_corner(self):
        corners = ["top-left", "top-right", "bottom-left", "bottom-right"]
        fields = [dict(kind="choice", key="widgetCorner", label="Corner", value="top-right",
                       options=[dict(value=c, label=c) for c in corners]),
                  dict(kind="button", key="add-widget", label="Add widget")]
        self.content.accept(json.dumps(dict(type="state", page="Widgets", pages=[], desktop={},
                                           sections=[dict(title="Add a widget", fields=fields)])))
        QTest.qWait(40)
        requests = []
        self.content.request.connect(lambda message: requests.append(message.toVariant()))
        def button(text):
            return next(item for item in visual_items(self.content) if item.isVisible()
                        and item.property("text") == text and item.metaObject().indexOfSignal(b"clicked()") >= 0)
        for corner in corners:
            button(corner).clicked.emit()
            QTest.qWait(20)
            self.assertTrue(button(corner).property("checked"))
            button("Add widget").clicked.emit()
            self.assertEqual(requests[-1]["action"], "add-widget")
            self.assertEqual(requests[-1]["fields"]["widgetCorner"], corner)

    def test_menu_style_goes_to_the_backend_and_chamber_follows_temperatures(self):
        style = dict(key="menuBarStyle", label="While printing, show", kind="radio", value="icon",
                     options=[dict(value="icon", label="Icon only"), dict(value="both", label="Progress and time left")])
        chamber = dict(key="panelShowsChamber", label="Chamber temperature", kind="toggle", value=True, enabled=False)
        state = dict(type="state", page="Menu Bar", pages=[], desktop={}, sections=[dict(title="Menu bar", fields=[style, chamber])])
        self.content.accept(json.dumps(state))
        QTest.qWait(30)

        radio = next((item for item in visual_items(self.window.contentItem())
                      if item.property("text") == "Progress and time left"
                      and item.metaObject().indexOfSignal(b"clicked()") >= 0), None)
        self.assertIsNotNone(radio)
        requests = []
        self.content.request.connect(lambda message: requests.append(message.toVariant()))
        radio.clicked.emit()
        self.assertEqual((requests[-1]["action"], requests[-1]["value"]), ("menuBarStyle", "both"))
        fields = [item for item in visual_items(self.window.contentItem())
                  if item.property("field") is not None and hasattr(item.property("field"), "toVariant")
                  and (item.property("field").toVariant() or {}).get("key") == "panelShowsChamber"]
        self.assertTrue(fields)
        self.assertTrue(all(not item.property("enabled") for item in fields))
        chamber["enabled"] = True
        self.content.accept(json.dumps(state))
        QTest.qWait(30)
        self.assertTrue(all(item.property("enabled") for item in fields))

    def test_widget_displays_a_target_only_when_it_differs(self):
        card = self.window.findChild(QObject, "widgetCard")
        card.setProperty("snapshot", dict(name="X1C", live=True, state="paused", caption="Paused",
            headline="Drone Frame", progress=58, stateLine="Paused by user", detail="", spools=[],
            temps=[dict(label=label, value=value, target=target) for label, value, target in (
                ("Nozzle", "250°", "250°"), ("Bed", "62°", "70°"), ("Chamber", "34°", None))]))
        card.setVisible(True)
        QTest.qWait(30)
        readings = {item.property("text") for item in visual_items(card) if item.isVisible()}
        self.assertTrue({"250°", "62° → 70°", "34°"}.issubset(readings))
        self.assertNotIn("250° → 250°", readings)

    def test_medium_widget_timing_stays_above_the_temperature_strip(self):
        card = self.window.findChild(QObject, "widgetCard")
        card.setProperty("snapshot", dict(name="Workshop", live=True, state="printing", caption="Printing",
            headline="A long print job that fills the available width", progress=78, layers="375/480",
            stateLine="52m left", finish="7:31 PM", temps=[dict(kind="nozzle", label="Nozzle", value="260°")]))
        card.setVisible(True)
        for width in (380, 432):
            card.setWidth(width)
            QTest.qWait(40)
            def label(text):
                return next(i for i in visual_items(card) if i.isVisible() and i.property("text") == text)
            reading = label("260°").mapToItem(card, QPointF()).y()
            for text in ("52m left", "Done 7:31 PM"):
                line = label(text)
                self.assertLess(line.mapToItem(card, QPointF(0, line.height())).y(), reading)

    def test_failed_large_widget_fits_issue_temperatures_and_all_ams_slots(self):
        card = self.window.findChild(QObject, "widgetCard")
        issue = "Spaghetti defects detected. " + "Check the model before continuing. " * 16
        card.setProperty("size", "large")
        card.setWidth(380)
        card.setHeight(380)
        card.setProperty("snapshot", dict(name="Workshop", live=True, state="failed", caption="Failed",
            headline="3DBenchy", stateLine=issue, issue=dict(text=issue), ams=dict(title="AMS Lite"),
            temps=[dict(kind="nozzle", label="Nozzle", value="28°")],
            spools=[dict(label=f"A{i}", name="PLA", amount=f"{i}00 g", color="#cf742d") for i in range(1, 5)]))
        card.setVisible(True)
        QTest.qWait(60)
        def label(text):
            return next(i for i in visual_items(card) if i.isVisible() and i.property("text") == text)
        def y(item, offset=0):
            return item.mapToItem(card, QPointF(0, offset)).y()
        notice = label(issue)
        self.assertTrue(notice.property("truncated"))
        self.assertEqual(notice.property("lineCount"), 3)
        self.assertGreater(y(notice), y(label("3DBenchy"), label("3DBenchy").height()) + 6)
        self.assertGreater(y(label("Nozzle")), y(notice, notice.height()) + 6)
        for i in range(1, 5):
            amount = label(f"{i}00 g")
            self.assertLessEqual(y(amount, amount.height()), card.height() - 15)

    def test_machine_cards_pair_at_desktop_width_and_stack_when_narrow(self):
        groups = [("Printer", "Speed", "speedometer"), ("Connection", "Network", "network-wireless"),
                  ("Specifications", "Build volume", "package"), ("Capabilities", "Bed", "heat-waves")]
        sections = [dict(title=title, fields=[dict(kind="text", key="", label=label, icon=icon, value="Test")])
                    for title, label, icon in groups]
        sections.append(dict(title="Last print", layout="history", fields=[dict(kind="text", key="", label="Drone Frame", value="Done")]))
        self.content.accept(json.dumps(dict(type="state", page="Printer", desktop={}, sections=sections)))
        for width, columns in ((1040, 2), (720, 1), (1040, 2)):
            self.window.setWidth(width)
            QTest.qWait(50)
            items = [i for i in visual_items(self.content) if i.isVisible()]
            cards = [i for i in items if i.property("fields") is not None]
            self.assertEqual(len(cards), 4)
            self.assertEqual(len({round(c.x()) for c in cards}), columns)
            self.assertEqual(len({round(c.y()) for c in cards}), 4 // columns)
            self.assertFalse(any(i.property("text") in ("Last print", "Drone Frame") for i in items))
            for _, label, icon in groups:
                self.assertTrue(any(i.property("symbolName") == icon for i in items))
                self.assertTrue(any(i.property("text") == label for i in items))

    def test_support_links_follow_live_license_changes(self):
        panel = self.window.findChild(QObject, "quickView")
        panel.setVisible(True)
        for supporter in (False, True, False):
            panel.setProperty("snapshot", dict(supporter=supporter))
            self.content.accept(json.dumps(dict(type="state", page="Printer", desktop={}, sections=[], supporter=supporter)))
            QTest.qWait(50)
            def visible_controls(parent):
                return [i for i in visual_items(parent) if i.isVisible() and i.property("field") is not None
                        and i.metaObject().indexOfSignal(b"clicked(QVariant)") >= 0]
            links = {i.property("text"): i for i in visible_controls(panel)}
            self.assertEqual("Support this project" in links, not supporter)
            self.assertEqual("Manage License…" in links, not supporter)
            if not supporter:
                for label, action in (("Support this project", "support-panel"), ("Manage License…", "license")):
                    links[label].clicked.emit(None)
                    self.assertEqual(panel.property("controller").property("lastAction"), action)
            title = "Hero Supporter" if supporter else "Support this project"
            header = next(i for i in visible_controls(self.content) if i.property("text") == title)
            field = header.property("field").toVariant()
            self.assertEqual(field["key"], "navigate:About" if supporter else "support-window")
            self.assertEqual(field["icon"], "starred" if supporter else "love")

    def test_quick_view_lines_up_with_an_empty_slot_and_dual_nozzles(self):
        panel = self.window.findChild(QObject, "quickView")
        slots = [dict(slot=f"A{i}", name="PETG" if i != 4 else "", loaded=i != 4,
                      remaining="≈ 570 g" if i != 4 else "Empty") for i in range(1, 5)]
        panel.setProperty("snapshot", dict(printer=dict(
            job="Drone Frame", paused=True, pause=True, stop=True, showControls=True,
            canSpeed=True, speeds=[dict(id=2, title="Standard")],
            temperatures=[dict(name=n, value="210° → 250°") for n in ("Left", "Right", "Bed", "Chamber")],
            ams=[dict(title="AMS A", spools=slots)])))
        panel.setVisible(True)
        inset = panel.property("contentInset")
        self.assertGreater(inset, 0)
        for width in (388, 420, 560):
            self.window.setWidth(width)
            QTest.qWait(40)
            items = list(visual_items(panel))
            def by_text(value):
                return next(i for i in items if i.isVisible() and i.property("text") == value)

            def left(item):
                return item.mapToItem(panel, QPointF()).x()

            def top(item):
                return item.mapToItem(panel, QPointF()).y()

            sensors = [i.parentItem().parentItem() for i in items
                       if i.isVisible() and i.property("text") == "210° → 250°"]
            self.assertEqual(len(sensors), 4)
            self.assertLessEqual(max(i.width() for i in sensors) - min(i.width() for i in sensors), 1)
            self.assertEqual(len({round(i.height()) for i in sensors}), 1)
            self.assertAlmostEqual(left(sensors[0]), inset, delta=1)
            self.assertAlmostEqual(left(sensors[-1]) + sensors[-1].width(), width - inset, delta=1)
            amounts = [i for i in items if i.isVisible() and i.property("text") in ("Empty", "≈ 570 g")]
            self.assertEqual(len(amounts), 4)
            self.assertEqual(len({round(top(i)) for i in amounts}), 1)
            cells = [i.parentItem() for i in amounts]
            self.assertLessEqual(max(i.width() for i in cells) - min(i.width() for i in cells), 1)
            first, last = cells[0], cells[-1]
            self.assertAlmostEqual(left(first), inset, delta=1)
            self.assertAlmostEqual(left(last) + last.width(), width - inset, delta=1)
            resume, stop = by_text("Resume"), by_text("Stop")
            self.assertAlmostEqual(resume.width(), stop.width(), delta=1)
            self.assertAlmostEqual(left(resume), inset, delta=1)
            speed = next(i for i in items if i.isVisible() and i.property("currentText") == "Standard")
            gauge = next(i for i in visual_items(speed) if i.property("symbolName") == "speedometer")
            self.assertGreater(left(gauge), left(speed))
            speed_label = next(i for i in visual_items(speed) if i.isVisible() and i.property("text") == "Standard")
            self.assertLess(left(gauge) + gauge.width(), left(speed_label))
            self.assertLess(left(speed_label), left(speed) + speed.width())
            self.assertAlmostEqual(speed.width(), stop.width(), delta=1)
            self.assertAlmostEqual(left(speed) + speed.width(), width - inset, delta=1)
            self.assertAlmostEqual(top(resume) + resume.height()/2, top(speed) + speed.height()/2, delta=1)


if __name__ == "__main__":
    unittest.main()
