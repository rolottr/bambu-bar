#!/usr/bin/env python3
"""Qt/Kirigami main window; camera remains on its private backend connection."""
import json
import configparser
import os
import sys
from pathlib import Path
from PyQt6.QtCore import QObject, QProcess, QUrl, pyqtSignal, pyqtSlot, pyqtProperty
from PyQt6.QtWidgets import QApplication
from PyQt6.QtGui import QColor, QPalette
from PyQt6.QtQml import QQmlApplicationEngine

ROOT = Path(__file__).resolve().parent
class Connection(QObject):
    message = pyqtSignal(str)
    def __init__(self):
        super().__init__()
        self.pending = bytearray()
        self.ready = False
        self.appearance = None
        self.process = QProcess(self)
        self.process.setProgram('/usr/bin/python3')
        self.process.setArguments([str(ROOT.parent / 'bridge.py')])
        self.process.readyReadStandardOutput.connect(self.read)
        self.process.start()
    def read(self):
        self.pending.extend(bytes(self.process.readAllStandardOutput()))
        while b'\n' in self.pending:
            line, _, rest = self.pending.partition(b'\n')
            self.pending[:] = rest
            self.message.emit(line.decode())
    @pyqtSlot(str)
    def send(self, message):
        self.process.write((message + '\n').encode())
    @pyqtSlot(int)
    def setAppearance(self, mode):
        if mode == self.appearance:
            return
        self.appearance = mode
        app = QApplication.instance()
        if mode == 2:
            app.setProperty('KDE_COLOR_SCHEME_PATH', '')
            app.setPalette(QPalette())
            return
        # KDE's integration reads this property on ApplicationPaletteChange,
        # including Kirigami's full semantic/disabled color sets and decorations.
        # Read the installed scheme; never write kdeglobals or invent a palette.
        scheme = 'BreezeDark.colors' if mode == 0 else 'BreezeLight.colors'
        paths = [Path(os.environ.get('XDG_DATA_HOME', str(Path.home() / '.local/share')))]
        paths += [Path(p) for p in os.environ.get('XDG_DATA_DIRS', '/usr/local/share:/usr/share').split(':')]
        path = next((p / 'color-schemes' / scheme for p in paths if (p / 'color-schemes' / scheme).is_file()), None)
        if path is None:
            return
        config = configparser.ConfigParser(interpolation=None)
        config.read(path)
        palette = QPalette()
        roles = {'Window': ('Window', 'BackgroundNormal'), 'WindowText': ('Window', 'ForegroundNormal'),
                 'Base': ('View', 'BackgroundNormal'), 'AlternateBase': ('View', 'BackgroundAlternate'),
                 'Text': ('View', 'ForegroundNormal'), 'Button': ('Button', 'BackgroundNormal'),
                 'ButtonText': ('Button', 'ForegroundNormal'), 'Highlight': ('Selection', 'BackgroundNormal'),
                 'HighlightedText': ('Selection', 'ForegroundNormal'), 'ToolTipBase': ('Tooltip', 'BackgroundNormal'),
                 'ToolTipText': ('Tooltip', 'ForegroundNormal'), 'Link': ('View', 'ForegroundLink'),
                 'LinkVisited': ('View', 'ForegroundVisited'), 'PlaceholderText': ('View', 'ForegroundInactive')}
        for role, (group, key) in roles.items():
            raw = config.get('Colors:' + group, key, fallback='')
            if raw:
                palette.setColor(getattr(QPalette.ColorRole, role), QColor(*map(int, raw.split(','))))
        app.setProperty('KDE_COLOR_SCHEME_PATH', str(path))
        app.setPalette(palette)
    @pyqtProperty(bool, constant=True)
    def demo(self):
        return bool(os.environ.get('BAMBUBAR_DEMO'))
    @pyqtProperty(str, constant=True)
    def initialPage(self):
        return os.environ.get('BAMBUBAR_PAGE', 'Printer')
    def close(self):
        self.process.closeWriteChannel()
        if not self.process.waitForFinished(1500):
            self.process.terminate()

app = QApplication(sys.argv)
app.setApplicationName('Bambu Bar')
app.setDesktopFileName('dev.rolottr.BambuBar')
app.setQuitOnLastWindowClosed(False)
bridge = Connection()
engine = QQmlApplicationEngine()
engine.rootContext().setContextProperty('backend', bridge)
engine.load(QUrl.fromLocalFile(str(ROOT / 'Main.qml')))
if not engine.rootObjects():
    bridge.close()
    sys.exit(1)
app.aboutToQuit.connect(bridge.close)
sys.exit(app.exec())
