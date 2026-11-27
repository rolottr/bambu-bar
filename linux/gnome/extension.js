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
import {DesktopWidgets} from './widgets.js';

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
        this._panelIcon = new St.Icon({icon_size: 18, style_class: 'system-status-icon'});
        this._panelIconKey = null;
        this._setPanelIcon('offline');
        panelBox.add_child(this._panelIcon);
        this._panelLabel = label();
        panelBox.add_child(this._panelLabel);
        this._indicator.add_child(panelBox);
        this._indicator.accessible_name = 'Bambu Bar';
        Main.panel.addToStatusArea(this.uuid, this._indicator);
        this._build();
        this._widgets = new DesktopWidgets((action, printer) => this._proxy?.InvokeRemote(action, printer, () => {}), key => this._t(key));
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
        menu.actor.add_style_class_name("bambu-menu");
        const item = new PopupMenu.PopupBaseMenuItem({reactive: true, activate: false, hover: false, can_focus: false});
        item.remove_style_class_name('popup-inactive-menu-item');
        item.add_style_class_name('bambu-container');
        const content = box(true, 'bambu-content');
        item.add_child(content);
        menu.addMenuItem(item);

        const heading = box(false, 'bambu-heading');
        const identity = box(true, 'bambu-identity');
        identity.x_expand = true;
        this._name = label('Bambu Bar', 'bambu-name');
        this._connection = label('', 'bambu-muted bambu-small');
        identity.add_child(this._name);
        const connectionRow = box(false, 'bambu-connection');
        this._connectionDot = new St.Widget({style_class: 'bambu-connection-dot', y_align: Clutter.ActorAlign.CENTER});
        connectionRow.add_child(this._connectionDot);
        connectionRow.add_child(this._connection);
        identity.add_child(connectionRow);
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
        bodyItem.add_style_class_name('bambu-container');
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
        const heroTop = box(false, 'bambu-hero-top');
        this._status = label('', 'bambu-status');
        heroTop.add_child(this._status);
        heroTop.add_child(new St.Widget({x_expand: true}));
        this._light = this._textButton('Light', () => this._invoke('light'));
        this._light.style_class = 'bambu-light bambu-action';
        this._light.x_expand = false;
        const lightContent = box(false, 'bambu-inline-icon');
        lightContent.add_child(new St.Icon({icon_name: 'display-brightness-symbolic', icon_size: 12}));
        this._lightText = label('Light');
        lightContent.add_child(this._lightText);
        this._light.set_child(lightContent);
        heroTop.add_child(this._light);
        this._stage = label('', 'bambu-muted bambu-small');
        const numbers = box(false, 'bambu-numbers');
        this._layers = label('', 'bambu-layer-count');
        this._job = label('', 'bambu-job-name');
        this._progress = label('', 'bambu-progress-number');
        this._progress.x_expand = true;
        numbers.add_child(this._progress);
        numbers.add_child(this._layers);
        this._rail = new St.Bin({style_class: 'bambu-progress-rail', x_expand: true});
        this._fill = new St.Widget({style_class: 'bambu-progress-fill', x_align: Clutter.ActorAlign.START, height: 4});
        this._rail.set_child(this._fill);
        this._rail.connect('notify::width', () => this._updateProgress());
        this._detail = label('', 'bambu-muted bambu-small');
        for (const child of [heroTop, this._job, this._stage, numbers, this._rail, this._detail])
            job.add_child(child);
        this._hero.add_child(job);
        this._body.add_child(this._hero);

        this._issue = box(false, 'bambu-issue');
        this._issue.add_child(new St.Icon({icon_name: 'dialog-information-symbolic', icon_size: 14, style_class: 'bambu-info', y_align: Clutter.ActorAlign.START}));
        const issueBody = box(true, 'bambu-issue-body');
        issueBody.x_expand = true;
        this._issueMessage = label('', 'bambu-issue-message');
        this._issueMessage.clutter_text.line_wrap = true;
        issueBody.add_child(this._issueMessage);
        const issueLinks = box(false, 'bambu-issue-links');
        this._issueCode = this._textButton('', () => St.Clipboard.get_default().set_text(St.ClipboardType.CLIPBOARD, this._state.printer?.issueId ?? ''));
        this._issueCode.style_class = 'bambu-code';
        this._issueCode.x_expand = false;
        this._wiki = this._textButton('Wiki', () => {
            const url = this._state.printer?.issueWiki;
            if (url && /^https:\/\/(wiki|e)\.bambulab\.com\//.test(url)) Gio.AppInfo.launch_default_for_uri(url, null);
        });
        this._wiki.style_class = 'bambu-link';
        this._wiki.x_expand = false;
        this._ignore = this._textButton('Ignore', () => {
            const id = this._state.printer?.issueId;
            if (id)
                this._invoke(`ignore-issue:${id}`);
        });
        this._ignore.style_class = 'bambu-link';
        this._ignore.x_expand = false;
        issueLinks.add_child(this._issueCode);
        issueLinks.add_child(this._wiki);
        issueLinks.add_child(this._ignore);
        issueBody.add_child(issueLinks);
        this._issue.add_child(issueBody);
        this._dismiss = this._iconButton('window-close-symbolic', 'Dismiss', 'dismiss-issue');
        this._dismiss.style_class = 'bambu-dismiss';
        this._dismiss.y_align = Clutter.ActorAlign.START;
        this._issue.add_child(this._dismiss);
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
        for (const child of [this._pause, this._stop, this._camera])
            this._controls.add_child(child);
        this._body.add_child(this._controls);
        this._locked = label('', 'bambu-muted bambu-small');
        this._locked.clutter_text.line_wrap = true;
        this._body.add_child(this._locked);

        this._speed = new PopupMenu.PopupSubMenuMenuItem('Speed');
        menu.addMenuItem(this._speed);

        const footerItem = new PopupMenu.PopupBaseMenuItem({reactive: true, activate: false, hover: false, can_focus: false});
        footerItem.add_style_class_name('bambu-container');
        const footerBody = box(true, 'bambu-content bambu-footer-body');
        const links = this._supportLinks = box(false, 'bambu-footer');
        this._support = this._textButton('Support this project', () => this._invoke('support-panel', true));
        const supportContent = box(false, 'bambu-inline-icon');
        supportContent.add_child(new St.Icon({icon_name: 'emote-love-symbolic', icon_size: 14}));
        this._supportText = label('Support this project');
        supportContent.add_child(this._supportText);
        this._support.set_child(supportContent);
        this._support.style_class = 'bambu-link';
        this._support.x_align = Clutter.ActorAlign.START;
        this._license = this._textButton('Manage License…', () => this._invoke('license', true));
        this._license.style_class = 'bambu-license';
        this._license.x_expand = false;
        links.add_child(this._support);
        links.add_child(this._license);
        footerBody.add_child(links);
        const footer = box(false, 'bambu-footer');
        this._open = this._textButton('Open Bambu Bar', () => this._invoke('show', true));
        this._open.style_class = 'bambu-footer-button';
        this._open.x_align = Clutter.ActorAlign.START;
        const openContent = box(false, 'bambu-inline-icon');
        openContent.add_child(new St.Icon({icon_name: 'window-new-symbolic', icon_size: 14}));
        this._openText = label('Open Bambu Bar');
        openContent.add_child(this._openText);
        this._open.set_child(openContent);
        this._quit = this._textButton('Quit', () => this._invoke('quit', true));
        this._quit.style_class = 'bambu-footer-button';
        this._quit.x_expand = false;
        footer.add_child(this._open);
        footer.add_child(this._quit);
        footerBody.add_child(footer);
        footerItem.add_child(footerBody);
        menu.addMenuItem(footerItem);
    }

    _iconButton(icon, title, action, close = false) {
        const button = new St.Button({style_class: 'button bambu-icon-button', can_focus: true, accessible_name: title,
            child: new St.Icon({icon_name: icon, icon_size: 16})});
        button.connect('clicked', () => this._invoke(action, close));
        return button;
    }
    _textButton(text, action) {
        const button = new St.Button({style_class: 'button bambu-action', can_focus: true, x_expand: true, label: text});
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
            this._setPanelIcon('offline');
            this._panelLabel.text = '';
            this._panelLabel.visible = false;
            this._widgets?.update([]);
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
    _setPanelIcon(key) {
        // Only packaged names may enter the path; old app versions use the idle glyph.
        if (!/^(offline|failed|(?:idle|paused|finished|printing-(?:[0-9]|1[0-9]|20))(?:-alert)?)$/.test(key))
            key = 'idle';
        if (this._panelIconKey === key)
            return;
        this._panelIconKey = key;
        const file = this.dir.get_child('tray-icons').get_child(`dev.rolottr.BambuBar-tray-${key}-symbolic.svg`);
        this._panelIcon.gicon = new Gio.FileIcon({file});
    }
    _receive(json) {
        if (!this._alive)
            return;
        try {
            const state = JSON.parse(json);
            if (state.version !== 1)
                return;
            this._state = state;
            this._widgets?.update(state.widgets ?? []);
            this._setPanelIcon(state.panelIcon ?? 'idle');
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
        this._openText.text = this._t('Open Bambu Bar');
        this._quit.label = this._t('Quit');
        this._supportLinks.visible = this._state.supporter === false;
        this._supportText.text = this._support.accessible_name = this._t('Support this project');
        this._license.label = this._t('Manage License…');

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
            this._picker.label.text = p?.name ?? this._t('Printer');
        }
        this._picker.visible = printers.length > 1;
        this._speed.visible = Boolean(p?.showControls);
        if (!p)
            return;

        this._connectionDot.style = `background-color: ${p.online ? '#32d074' : '#e35b58'};`;
        this._status.text = `● ${p.state}`;
        this._status.style_class = `bambu-status bambu-state-${p.kind}`;
        this._job.text = p.job;
        this._progress.text = p.online ? `${p.progress}%` : '—';
        this._updateProgress();
        this._stage.text = p.stage ?? p.detail;
        this._layers.text = p.totalLayers > 0 ? `${p.layer}/${p.totalLayers}\n${this._t('layers')}` : '';
        this._detail.text = p.kind === 'failed' ? `⚠ ${this._t('Needs attention')}` : p.detail;
        this._detail.style_class = p.kind === 'failed' ? 'bambu-attention' : 'bambu-muted bambu-small';
        this._issueMessage.text = p.issue;
        this._issueCode.label = p.issueId ?? '';
        this._issueCode.accessible_name = this._t('Copy code');
        this._wiki.visible = Boolean(p.issueWiki);
        this._ignore.label = this._t('Ignore');
        this._ignore.accessible_name = this._t('Ignore');
        this._ignore.visible = Boolean(p.issueId);
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
                tile.style = `width: ${Math.floor((348 - 8 * (p.temperatures.length - 1)) / p.temperatures.length) - 22}px;`;
                tile.add_child(label(`♨ ${temp.name}`, 'bambu-small bambu-muted'));
                tile.add_child(label(temp.value, 'bambu-temperature-value'));
                this._temperatures.add_child(tile);
            }
        }
        const spoolKey = JSON.stringify([p.ams, p.external]);
        if (this._spoolKey !== spoolKey) {
            this._spoolKey = spoolKey;
            this._spools.destroy_all_children();
            const units = p.ams ?? [];
            for (let index = 0; index < units.length; index++) {
                const unit = units[index];
                const card = box(true, 'bambu-ams');
                const head = box(false, 'bambu-ams-heading');
                const title = label(unit.title.toUpperCase(), 'bambu-ams-title');
                title.x_expand = true;
                head.add_child(title);
                const climate = [unit.humidity != null ? `${unit.humidity}% RH` : '', unit.temperature != null ? `${Math.round(unit.temperature)}°` : ''].filter(Boolean).join(' · ');
                head.add_child(label(climate, 'bambu-muted bambu-small'));
                card.add_child(head);
                const spools = box(false, 'bambu-spool-row');
                for (const spool of unit.spools) spools.add_child(this._spool(spool));
                if (index === units.length - 1 && p.external) {
                    spools.add_child(new St.Widget({style_class: 'bambu-spool-divider'}));
                    spools.add_child(this._spool(p.external));
                }
                card.add_child(spools);
                this._spools.add_child(card);
            }
            if (!units.length && p.external) {
                const card = box(false, 'bambu-ams');
                card.add_child(this._spool(p.external));
                this._spools.add_child(card);
            }
        }
        this._temperatures.visible = Boolean(p.temperatures?.length);
        this._spools.visible = Boolean(p.ams?.length || p.external);
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
        this._controls.visible = Boolean(p.showControls || p.camera);
        this._camera.accessible_name = this._t('Camera');
        this._camera.label = this._t('Camera');
        this._light.visible = Boolean(p.light);
        this._lightText.text = this._t('Light');
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

    _updateProgress() {
        this._fill.width = Math.max(0, Math.min(1, (this._state.printer?.progress ?? 0) / 100)) * this._rail.width;
    }

    _spool(spool) {
        const column = box(true, 'bambu-spool');
        column.x_expand = true;
        column.accessible_name = `${spool.slot}: ${spool.detail}`;
        const color = /^[0-9a-f]{6}/i.test(spool.color ?? '') ? spool.color.slice(0, 6) : '888888';
        const rim = new St.Bin({style_class: 'bambu-spool-rim', x_align: Clutter.ActorAlign.CENTER});
        const disc = new St.Bin({style_class: 'bambu-spool-disc', style: `background-color: #${color};`});
        disc.set_child(new St.Widget({style_class: 'bambu-spool-hub', x_align: Clutter.ActorAlign.CENTER, y_align: Clutter.ActorAlign.CENTER}));
        rim.set_child(disc);
        column.add_child(rim);
        column.add_child(label(spool.loaded ? spool.name : '—', 'bambu-spool-material'));
        column.add_child(label(spool.remaining, 'bambu-spool-amount bambu-muted'));
        return column;
    }

    disable() {
        this._alive = false;
        this._widgets?.destroy();
        this._widgets = null;
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
