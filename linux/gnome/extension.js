// SPDX-License-Identifier: MIT
// The app owns connections, credentials and commands. Shell only paints cached status and forwards explicit actions.
import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Pango from 'gi://Pango';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

const BUS = 'dev.rolottr.BambuBar.Desktop';
const PATH = '/dev/rolottr/BambuBar/Desktop';
const XML = `<node><interface name="${BUS}">
<method name="GetState"><arg type="s" direction="out" name="state"/></method>
<method name="SetOpen"><arg type="b" direction="in" name="open"/></method>
<method name="Invoke"><arg type="s" direction="in" name="action"/><arg type="s" direction="in" name="printer"/></method>
<signal name="StateChanged"><arg type="s" name="state"/></signal>
</interface></node>`;
const DesktopProxy = Gio.DBusProxy.makeProxyWrapper(XML);

function label(text = '', style = '') {
    const actor = new St.Label({text, style_class: style, y_align: Clutter.ActorAlign.CENTER});
    actor.clutter_text.ellipsize = Pango.EllipsizeMode.END;
    return actor;
}
function box(vertical = false, style = '') {
    return new St.BoxLayout({orientation: vertical ? Clutter.Orientation.VERTICAL : Clutter.Orientation.HORIZONTAL, style_class: style});
}

export default class BambuBarExtension extends Extension {
    enable() {
        this._alive = true;
        this._state = {};
        this._pickerKey = this._tempKey = this._spoolKey = this._artPath = this._speedKey = null;
        this._cancellable = new Gio.Cancellable();
        this._indicator = new PanelMenu.Button(0.5, 'Bambu Bar', false);
        const panelBox = box(false, 'bambu-panel');
        const iconFile = this.dir.get_child('bambu-bar-symbolic.svg');
        panelBox.add_child(new St.Icon({gicon: new Gio.FileIcon({file: iconFile}), style_class: 'system-status-icon'}));
        this._panelLabel = label();
        panelBox.add_child(this._panelLabel);
        this._indicator.add_child(panelBox);
        this._indicator.accessible_name = 'Bambu Bar';
        Main.panel.addToStatusArea(this.uuid, this._indicator);
        this._build();
        this._indicator.menu.connect('open-state-changed', (_menu, open) => {
            if (this._proxy?.g_name_owner)
                this._proxy.SetOpenRemote(open, () => {});
            if (open)
                this._paint();
        });
        this._proxy = new DesktopProxy(Gio.DBus.session, BUS, PATH, (proxy, error) => {
            if (!this._alive || error)
                return;
            this._ownerChanged = proxy.connect('notify::g-name-owner', () => this._refresh());
            this._signal = proxy.connectSignal('StateChanged', (_proxy, _sender, [json]) => this._receive(json));
            this._refresh();
        }, this._cancellable);
        // The app's generic SNI icon becomes passive while this native integration is alive.
        this._busOwner = Gio.bus_own_name(Gio.BusType.SESSION, 'dev.rolottr.BambuBar.Shell', Gio.BusNameOwnerFlags.NONE, null, null, null);
        this._paint();
    }

    _build() {
        const menu = this._indicator.menu;
        const item = new PopupMenu.PopupBaseMenuItem({reactive: true, activate: false, hover: false, can_focus: false});
        item.remove_style_class_name('popup-inactive-menu-item');
        const content = box(true, 'bambu-content');
        item.add_child(content);
        menu.addMenuItem(item);

        const heading = box(false, 'bambu-heading');
        const identity = box(true, 'bambu-identity');
        identity.x_expand = true;
        this._name = label('Bambu Bar', 'bambu-name');
        this._connection = label('', 'bambu-muted bambu-small');
        identity.add_child(this._name);
        identity.add_child(this._connection);
        heading.add_child(identity);
        this._refreshButton = this._iconButton('view-refresh-symbolic', 'Refresh', 'refresh');
        heading.add_child(this._refreshButton);
        this._settingsButton = this._iconButton('emblem-system-symbolic', 'Settings', 'settings', true);
        heading.add_child(this._settingsButton);
        content.add_child(heading);

        this._picker = new PopupMenu.PopupSubMenuMenuItem('');
        menu.addMenuItem(this._picker);

        const bodyItem = new PopupMenu.PopupBaseMenuItem({reactive: true, activate: false, hover: false, can_focus: false});
        bodyItem.remove_style_class_name('popup-inactive-menu-item');
        this._body = box(true, 'bambu-content bambu-body');
        this._bodyScroll = new St.ScrollView({child: this._body, hscrollbar_policy: St.PolicyType.NEVER,
            vscrollbar_policy: St.PolicyType.AUTOMATIC, overlay_scrollbars: true});
        bodyItem.add_child(this._bodyScroll);
        menu.addMenuItem(bodyItem);

        this._hero = box(false, 'bambu-hero');
        this._art = new St.Icon({icon_size: 132, style_class: 'bambu-art', y_align: Clutter.ActorAlign.CENTER});
        this._hero.add_child(this._art);
        const job = box(true, 'bambu-job');
        job.x_expand = true;
        job.y_align = Clutter.ActorAlign.CENTER;
        this._status = label('', 'bambu-status');
        this._job = label('', 'bambu-job-name');
        this._progress = label('', 'bambu-progress-number');
        this._rail = new St.Bin({style_class: 'bambu-progress-rail', x_expand: true});
        this._fill = new St.Widget({style_class: 'bambu-progress-fill', x_align: Clutter.ActorAlign.START, height: 4});
        this._rail.set_child(this._fill);
        this._detail = label('', 'bambu-muted bambu-small');
        for (const child of [this._status, this._job, this._progress, this._rail, this._detail])
            job.add_child(child);
        this._hero.add_child(job);
        this._body.add_child(this._hero);

        this._issue = label('', 'bambu-issue bambu-small');
        this._issue.clutter_text.line_wrap = true;
        this._issue.clutter_text.line_wrap_mode = Pango.WrapMode.WORD_CHAR;
        this._body.add_child(this._issue);
        this._temperatures = box(false, 'bambu-temperatures');
        this._body.add_child(this._temperatures);
        this._spools = box(true, 'bambu-spools');
        this._body.add_child(this._spools);
        this._fans = label('', 'bambu-small bambu-muted');
        this._body.add_child(this._fans);

        this._controls = box(false, 'bambu-actions');
        this._pause = this._textButton('Pause', () => this._invoke(this._state.printer?.paused ? 'resume' : 'pause'));
        this._stop = this._textButton('Stop', () => this._invoke('stop', true));
        this._camera = this._textButton('Camera', () => this._invoke('camera', true));
        this._light = this._textButton('Light', () => this._invoke('light'));
        for (const child of [this._pause, this._stop, this._camera, this._light])
            this._controls.add_child(child);
        this._body.add_child(this._controls);
        this._locked = label('', 'bambu-muted bambu-small');
        this._locked.clutter_text.line_wrap = true;
        this._body.add_child(this._locked);

        this._speed = new PopupMenu.PopupSubMenuMenuItem('Speed');
        menu.addMenuItem(this._speed);

        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._open = new PopupMenu.PopupImageMenuItem('Open Bambu Bar', 'window-new-symbolic');
        this._open.connect('activate', () => this._invoke('show', true));
        menu.addMenuItem(this._open);
        this._quit = new PopupMenu.PopupImageMenuItem('Quit', 'application-exit-symbolic');
        this._quit.connect('activate', () => this._invoke('quit', true));
        menu.addMenuItem(this._quit);
    }

    _iconButton(icon, title, action, close = false) {
        const button = new St.Button({style_class: 'bambu-icon-button', can_focus: true, accessible_name: title,
            child: new St.Icon({icon_name: icon, icon_size: 16})});
        button.connect('clicked', () => this._invoke(action, close));
        return button;
    }
    _textButton(text, action) {
        const button = new St.Button({style_class: 'bambu-action', can_focus: true, x_expand: true, label: text});
        button.connect('clicked', action);
        return button;
    }
    _t(key) { return this._state.labels?.[key] ?? key; }
    _invoke(action, close = false) {
        if (close)
            this._indicator.menu.close();
        if (this._proxy?.g_name_owner) {
            this._proxy.InvokeRemote(action, this._state.printer?.id ?? '', () => {});
        } else if (action !== 'quit') {
            Gio.DesktopAppInfo.new('dev.rolottr.BambuBar.desktop')?.launch([], null);
        }
    }
    _refresh() {
        if (!this._alive)
            return;
        if (!this._proxy?.g_name_owner) {
            this._state = {};
            this._paint();
            return;
        }
        this._proxy.GetStateRemote((result, error) => {
            if (this._alive && !error && result) {
                this._receive(result[0]);
                if (this._indicator.menu.isOpen)
                    this._proxy.SetOpenRemote(true, () => {});
            }
        });
    }
    _receive(json) {
        if (!this._alive)
            return;
        try {
            const state = JSON.parse(json);
            if (state.version !== 1)
                return;
            this._state = state;
            this._panelLabel.text = state.panelText ?? '';
            this._panelLabel.visible = this._panelLabel.text.length > 0;
            this._indicator.accessible_name = state.title ?? 'Bambu Bar';
            if (this._indicator.menu.isOpen)
                this._paint();
        } catch (error) {
            console.error('Bambu Bar: invalid desktop state', error);
        }
    }
    _paint() {
        const p = this._state.printer;
        this._name.text = p?.name ?? 'Bambu Bar';
        this._connection.text = p ? `${p.model} · ${p.link}` : this._t('Not connected');
        this._refreshButton.visible = Boolean(p);
        this._refreshButton.accessible_name = this._t('Refresh');
        this._settingsButton.accessible_name = this._t('Settings');
        this._body.visible = Boolean(p);
        this._bodyScroll.visible = Boolean(p);
        const monitor = Main.layoutManager.findMonitorForActor(this._indicator) ?? Main.layoutManager.primaryMonitor;
        this._bodyScroll.style = `max-height: ${Math.max(160, (monitor?.height ?? 1080) - 250)}px;`;
        this._quit.visible = Boolean(this._proxy?.g_name_owner);
        this._open.label.text = this._t('Open Bambu Bar');
        this._quit.label.text = this._t('Quit');

        const printers = this._state.printers ?? [];
        const pickerKey = JSON.stringify(printers) + p?.id;
        if (this._pickerKey !== pickerKey) {
            this._pickerKey = pickerKey;
            this._picker.menu.removeAll();
            for (const printer of printers) {
                const item = new PopupMenu.PopupMenuItem(`${printer.name} · ${printer.state}`);
                item.setOrnament(printer.id === p?.id ? PopupMenu.Ornament.DOT : PopupMenu.Ornament.NONE);
                item.connect('activate', () => this._proxy?.InvokeRemote('select', printer.id, () => {}));
                this._picker.menu.addMenuItem(item);
            }
            this._picker.label.text = this._t('All Printers');
        }
        this._picker.visible = printers.length > 1;
        this._speed.visible = Boolean(p?.showControls);
        if (!p)
            return;

        this._status.text = p.state;
        this._status.style_class = `bambu-status bambu-state-${p.kind}`;
        this._job.text = p.job;
        this._progress.text = p.online ? `${p.progress}%` : '—';
        this._fill.width = Math.max(0, Math.min(1, p.progress / 100)) * 180;
        this._detail.text = p.detail;
        this._issue.text = p.issue;
        this._issue.visible = Boolean(p.issue);
        if (p.art && this._artPath !== p.art) {
            this._artPath = p.art;
            this._art.gicon = new Gio.FileIcon({file: Gio.File.new_for_path(p.art)});
        }
        this._art.visible = Boolean(p.art);
        const tempKey = JSON.stringify(p.temperatures);
        if (this._tempKey !== tempKey) {
            this._tempKey = tempKey;
            this._temperatures.destroy_all_children();
            for (const temp of p.temperatures ?? []) {
                const tile = box(true, 'bambu-temperature');
                tile.x_expand = true;
                tile.add_child(label(temp.name, 'bambu-small bambu-muted'));
                tile.add_child(label(temp.value, 'bambu-temperature-value'));
                this._temperatures.add_child(tile);
            }
        }
        const spoolKey = JSON.stringify(p.spools);
        if (this._spoolKey !== spoolKey) {
            this._spoolKey = spoolKey;
            this._spools.destroy_all_children();
            for (const spool of p.spools ?? []) {
                const row = box(false, 'bambu-spool');
                const color = /^[0-9a-f]{6}/i.test(spool.color ?? '') ? spool.color.slice(0, 6) : '888888';
                row.add_child(new St.Widget({style_class: 'bambu-spool-dot', style: `background-color: #${color};`, y_align: Clutter.ActorAlign.CENTER}));
                const name = label(`${spool.slot}  ${spool.name}`, 'bambu-small');
                name.x_expand = true;
                row.add_child(name);
                row.add_child(label(spool.remaining, 'bambu-muted bambu-small'));
                this._spools.add_child(row);
            }
        }
        this._temperatures.visible = Boolean(p.temperatures?.length);
        this._spools.visible = Boolean(p.spools?.length);
        this._fans.text = p.fans;
        this._fans.visible = Boolean(p.fans);
        this._pause.label = this._t(p.paused ? 'Resume' : 'Pause');
        this._stop.label = this._t('Stop');
        this._pause.visible = this._stop.visible = Boolean(p.showControls);
        this._pause.reactive = this._pause.can_focus = Boolean(p.pause);
        this._stop.reactive = this._stop.can_focus = Boolean(p.stop);
        this._pause.opacity = p.pause ? 255 : 95;
        this._stop.opacity = p.stop ? 255 : 95;
        this._camera.visible = Boolean(p.camera);
        this._camera.accessible_name = this._t('Camera');
        this._camera.label = this._t('Camera');
        this._light.visible = Boolean(p.light);
        this._light.label = this._t('Light');
        this._light.accessible_name = this._t(p.lightOn ? 'Turn light off' : 'Turn light on');
        if (p.lightOn)
            this._light.add_style_pseudo_class('checked');
        else
            this._light.remove_style_pseudo_class('checked');
        this._locked.text = this._t('Enable Developer Mode on the printer to use controls');
        this._locked.visible = Boolean(p.locked && p.showControls);
        this._speed.visible = Boolean(p.showControls);
        this._speed.setSensitive(Boolean(p.canSpeed));
        const speedKey = JSON.stringify(p.speeds) + p.speed;
        if (this._speedKey !== speedKey) {
            this._speedKey = speedKey;
            this._speed.menu.removeAll();
            for (const speed of p.speeds ?? []) {
                const choice = new PopupMenu.PopupMenuItem(speed.title);
                choice.setOrnament(speed.id === p.speed ? PopupMenu.Ornament.DOT : PopupMenu.Ornament.NONE);
                choice.connect('activate', () => this._invoke(`speed:${speed.id}`));
                this._speed.menu.addMenuItem(choice);
            }
            const current = p.speeds?.find(s => s.id === p.speed)?.title ?? '';
            this._speed.label.text = `${this._t('Speed')} · ${current}`;
        }
    }

    disable() {
        this._alive = false;
        this._cancellable?.cancel();
        if (this._proxy) {
            if (this._proxy.g_name_owner)
                this._proxy.SetOpenRemote(false, () => {});
            if (this._signal)
                this._proxy.disconnectSignal(this._signal);
            if (this._ownerChanged)
                this._proxy.disconnect(this._ownerChanged);
            this._proxy = null;
        }
        if (this._busOwner)
            Gio.bus_unown_name(this._busOwner);
        this._busOwner = 0;
        this._indicator?.destroy();
        this._indicator = null;
    }
}
