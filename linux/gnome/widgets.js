// GNOME desktop cards. Live data comes from the app; never connect to printers from Shell.
import Clutter from 'gi://Clutter';
import Meta from 'gi://Meta';
import St from 'gi://St';
import Pango from 'gi://Pango';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {placeWidgets} from './widget-layout.js';
import {widgetModel} from './widget-model.js';

function label(text, style = '') {
    const l = new St.Label({text: text ?? '', style_class: style});
    l.clutter_text.ellipsize = Pango.EllipsizeMode.END;
    return l;
}
function column(style = '') { return new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: style}); }
function spoolDots(spools) {
    const row = new St.BoxLayout({style_class: 'bambu-widget-idle-spools'});
    for (const spool of (spools ?? []).filter(s => !s.empty).slice(0, 5)) {
        const color = (spool.color ?? '').replace('#', '').slice(0, 6);
        row.add_child(new St.Widget({style_class: 'bambu-widget-idle-spool',
            style: `background-color: #${/^[0-9a-f]{6}$/i.test(color) ? color : '888888'};`}));
    }
    return row;
}
function temperatureIcon(temp) {
    const color = /^#[0-9a-f]{6}$/i.test(temp.color) ? temp.color : '#bdbdc4';
    const path = temp.kind === 'bed' ? 'M5 19c-5-5 5-5 0-10m7 10c-5-5 5-5 0-10m7 10c-5-5 5-5 0-10' : temp.kind === 'chamber' ? 'M10 3a2 2 0 0 1 4 0v10a4 4 0 1 1-4 0V3m2 4v10' : 'M12 2c2 6 6 6 6 12a6 6 0 0 1-12 0c0-3 2-5 3-7 0 4 2 4 3 5 2-3 0-6 0-10';
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><path d="${path}" fill="none" stroke="${color}" stroke-width="2" stroke-linecap="round"/></svg>`;
    return new St.Icon({gicon: new Gio.BytesIcon({bytes: new GLib.Bytes(new TextEncoder().encode(svg))}), icon_size: 12});
}
export class DesktopWidgets {
    constructor(invoke, translate = text => text) {
        this._invoke = invoke;
        this._t = translate;
        this._cards = new Map();
        this._signals = [];
        for (const [object, signal] of [[global.display, 'restacked'], [Main.layoutManager, 'monitors-changed'], [Main.overview, 'showing'], [Main.overview, 'hidden'], [Main.sessionMode, 'updated']])
            this._signals.push([object, object.connect(signal, () => this._layout())]);
    }
    update(widgets) {
        const keep = new Set(widgets.map(w => w.id));
        let changed = false;
        for (const [id, card] of this._cards) if (!keep.has(id)) { card.actor.destroy(); this._cards.delete(id); changed = true; }
        for (const widget of widgets) {
            // SavedAt changes on every report and has no visual effect.
            const key = JSON.stringify({...widget, state: {...widget.state, savedAt: 0, updatedAt: 0}});
            if (this._cards.get(widget.id)?.key === key) continue;
            this._cards.get(widget.id)?.actor.destroy();
            const actor = this._build(widget);
            global.window_group.add_child(actor);
            this._cards.set(widget.id, {actor, key, widget});
            changed = true;
        }
        if (changed) this._layout();
    }
    _build(widget) {
        const state = widget.state;
        const model = widgetModel(state);
        const small = widget.size === 'small';
        const large = widget.size === 'large';
        const content = column('bambu-widget-content');
        content.x_expand = content.y_expand = true;
        const header = new St.BoxLayout({style_class: 'bambu-widget-header'});
        const name = label(state.name || 'Bambu Bar', 'bambu-widget-name'); name.x_expand = true;
        header.add_child(name);
        const caption = label(`● ${state.caption}`, 'bambu-widget-caption');
        caption.style = `color: ${model.color};`;
        header.add_child(caption);
        const info = column('bambu-widget-info'); info.x_expand = true;
        if (small) {
            const top = new St.BoxLayout({style_class: 'bambu-widget-small-top'});
            const ring = new St.Widget({layout_manager: new Clutter.BinLayout(), width: 58, height: 58});
            const tint = state.state === 'failed' || state.state === 'finished' ? model.color : model.tint;
            const fraction = state.state === 'finished' ? 100 : model.progress;
            const arc = `<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><circle cx="50" cy="50" r="42" fill="none" stroke="#45484b" stroke-width="10"/><circle cx="50" cy="50" r="42" fill="none" stroke="${tint}" stroke-width="10" stroke-linecap="round" stroke-opacity="${fraction > 0 ? 1 : 0}" stroke-dasharray="${fraction * 2.639} 264" transform="rotate(-90 50 50)"/></svg>`;
            ring.add_child(new St.Icon({gicon: new Gio.BytesIcon({bytes: new GLib.Bytes(new TextEncoder().encode(arc))}), icon_size: 58}));
            const dial = !model.live ? '—' : state.state === 'finished' ? '✓' : state.state === 'failed' ? (state.canceled ? '×' : '!') : state.state === 'preparing' ? '♨' : `${model.progress}%`;
            const dialLabel = label(dial, 'bambu-widget-progress small'); dialLabel.x_align = dialLabel.y_align = Clutter.ActorAlign.CENTER; if (['failed', 'finished'].includes(state.state)) dialLabel.style = `color: ${model.color};`; ring.add_child(dialLabel); top.add_child(ring);
            const primary = column('bambu-widget-small-primary'); primary.x_expand = true;
            const stateLabel = label(state.caption.toUpperCase(), 'bambu-widget-caption'); stateLabel.style = `color: ${model.color};`; primary.add_child(stateLabel);
            if (state.state === 'idle' && model.live) {
                for (const temp of state.temps ?? []) {
                    const line = new St.BoxLayout({style_class: 'bambu-widget-inline-temp'}); line.add_child(temperatureIcon(temp)); line.add_child(label(temp.value, 'bambu-widget-detail strong')); primary.add_child(line);
                }
            } else {
                let value = state.reason || (model.live && ['failed', 'finished'].includes(state.state) ? state.lastJob?.ago || this._t(state.state === 'failed' ? 'Stopped' : 'Done') : state.stateLine) || '—';
                const left = state.strings?.leftWord;
                if (state.state === 'printing' && left && value.endsWith(left)) value = value.slice(0, -left.length).trim();
                const summary = label(value, 'bambu-widget-small-value'); summary.clutter_text.line_wrap = true; primary.add_child(summary);
                if (state.state === 'printing' && left) primary.add_child(label(left, 'bambu-widget-detail'));
            }
            top.add_child(primary); content.add_child(top);
            content.add_child(new St.Widget({y_expand: true}));
            if (model.active && state.activeSpoolLine) {
                const active = new St.BoxLayout({style_class: 'bambu-widget-inline-temp'});
                active.add_child(spoolDots((state.spools ?? []).filter(s => s.active))); active.add_child(label(state.activeSpoolLine, 'bambu-widget-detail')); content.add_child(active);
                content.add_child(new St.Widget({y_expand: true}));
            } else if (state.state === 'idle') content.add_child(spoolDots(state.spools));
            content.add_child(label(state.headline, 'bambu-widget-job'));
            if (state.detail) content.add_child(label(state.detail, 'bambu-widget-detail'));
        } else {
            const hero = new St.BoxLayout({style_class: 'bambu-widget-hero', x_expand: true});
            const art = new St.Bin({style_class: 'bambu-widget-art', width: 92, height: 110, y_align: Clutter.ActorAlign.CENTER});
            if (state.art && GLib.path_is_absolute(state.art)) {
                const file = Gio.File.new_for_path(state.art);
                art.set_child(new St.Icon({gicon: new Gio.FileIcon({file}), icon_size: 108}));
            } else art.set_child(new St.Icon({icon_name: 'printer-symbolic', icon_size: 60, style_class: 'bambu-widget-detail'}));
            hero.add_child(art); info.add_child(header);
            info.add_child(label(state.headline, 'bambu-widget-job'));
            const numbers = new St.BoxLayout({style_class: 'bambu-widget-numbers'});
            const percent = label(model.live ? `${model.progress}%` : '—', 'bambu-widget-progress'); percent.x_expand = true; numbers.add_child(percent);
            numbers.add_child(label(state.layers ?? '', 'bambu-widget-detail'));
            if (model.active) info.add_child(numbers);
            const rail = new St.Bin({style_class: 'bambu-widget-rail', x_expand: true});
            const fill = new St.Widget({style_class: 'bambu-widget-fill', x_align: Clutter.ActorAlign.START, height: 5, style: `background-color: ${model.tint};`});
            rail.set_child(fill);
            rail.connect('notify::width', () => { fill.width = rail.width * model.progress / 100; });
            if (model.active) info.add_child(rail);
            const detail = label(large ? model.detail : state.stateLine, 'bambu-widget-detail strong');
            const timing = new St.BoxLayout({style_class: 'bambu-widget-timing'}); detail.x_expand = true;
            if (detail.text) timing.add_child(detail);
            if (state.finish) timing.add_child(label((state.strings?.done || '{0}').replace('{0}', state.finish), 'bambu-widget-detail'));
            info.add_child(timing);
            if (!model.active && model.live) info.add_child(spoolDots(state.spools));
            hero.add_child(info); content.add_child(hero);
            const temperatures = new St.BoxLayout({style_class: large ? 'bambu-widget-temps' : 'bambu-widget-temps compact'});
            for (const temp of state.temps ?? []) {
                const tile = large ? column('bambu-widget-temp') : new St.BoxLayout({style_class: 'bambu-widget-inline-temp'});
                tile.x_expand = true;
                const name = label(temp.label, 'bambu-widget-detail');
                name.style = `color: ${/^#[0-9a-f]{6}$/i.test(temp.color) ? temp.color : '#bdbdc4'};`;
                tile.add_child(large ? name : temperatureIcon(temp));
                tile.add_child(label(temp.value, large ? 'bambu-widget-temperature' : 'bambu-widget-detail strong')); temperatures.add_child(tile);
            }
            if (large || model.active) content.add_child(temperatures);
        }
        if (large) {
            const ams = column('bambu-widget-ams');
            if (state.ams) ams.add_child(label([state.ams.title, state.ams.humidity].filter(Boolean).join(' · '), 'bambu-widget-detail'));
            const spools = new St.BoxLayout({style_class: 'bambu-widget-spools'});
            for (const spool of state.spools ?? []) {
                const item = column('bambu-widget-spool'); item.x_expand = true;
                const rim = new St.Bin({style_class: 'bambu-widget-spool-rim', x_align: Clutter.ActorAlign.CENTER});
                if (spool.active && model.active) rim.style = 'border-color: #21cc6b;';
                const color = spool.color.replace('#', '').slice(0, 6);
                const disc = new St.Bin({style_class: 'bambu-widget-spool-ring', style: `background-color: #${/^[0-9a-f]{6}$/i.test(color) ? color : '888888'};`});
                disc.set_child(new St.Widget({style_class: 'bambu-widget-spool-hub', x_align: Clutter.ActorAlign.CENTER, y_align: Clutter.ActorAlign.CENTER})); rim.set_child(disc);
                item.add_child(rim); item.add_child(label(spool.name || spool.label, 'bambu-widget-detail strong')); item.add_child(label(spool.amount, 'bambu-widget-detail')); spools.add_child(item);
            }
            ams.add_child(spools); content.add_child(ams);
            content.add_child(new St.Widget({y_expand: true}));
            if (model.issue) {
                const issue = label(`⚠ ${model.issue}`, 'bambu-widget-issue');
                issue.clutter_text.line_wrap = true; content.add_child(issue);
            }
            else if (state.lastJob) {
                const footer = new St.BoxLayout(); const job = label((state.strings?.last || 'Last: {0}').replace('{0}', state.lastJob.name), 'bambu-widget-detail strong'); job.x_expand = true; footer.add_child(job); footer.add_child(label(state.lastJob.ago, 'bambu-widget-detail')); content.add_child(footer);
            }
        }
        const [width, height] = model.dimensions[widget.size] ?? model.dimensions.medium;
        const actor = new St.Button({style_class: `popup-menu-content bambu-desktop-widget ${widget.size}`, can_focus: true, reactive: true, child: content,
            width, height, x_align: Clutter.ActorAlign.FILL, y_align: Clutter.ActorAlign.FILL,
            accessible_name: `${state.name}: ${state.caption}. Open printer.`});
        actor.connect('clicked', () => this._invoke('show', state.serial));
        return actor;
    }
    _layout() {
        if (Main.layoutManager.primaryIndex < 0) return;
        const area = Main.layoutManager.getWorkAreaForMonitor(Main.layoutManager.primaryIndex);
        if (!area) return;
        const cards = [...this._cards.values()];
        const positions = placeWidgets(cards.map(({actor, widget}) => {
            const width = Math.min(widget.size === 'small' ? 180 : 380, Math.max(160, area.width - 40));
            if (actor.width !== width) actor.width = width;
            return {width: actor.width, height: actor.get_preferred_height(actor.width)[1], corner: widget.corner};
        }), area);
        // Desktop icon surfaces must stay below the cards; ordinary windows stay above them.
        const children = global.window_group.get_children();
        const windows = children.filter(a => a.get_meta_window?.());
        const firstWindow = windows.find(a => {
            const w = a.get_meta_window();
            return w.get_window_type() !== Meta.WindowType.DESKTOP && w.get_gtk_application_id?.() !== 'com.rastersoft.ding';
        });
        for (const [index, {actor}] of cards.entries()) {
            actor.visible = !Main.overview.visible && !Main.sessionMode.isLocked;
            if (actor.x !== positions[index].x || actor.y !== positions[index].y)
                actor.set_position(positions[index].x, positions[index].y);
            // Reordering an already-correct stack repaints the whole desktop on each report.
            // Only move cards that crossed a real window or the desktop-icons surface.
            const siblings = global.window_group.get_children();
            const actorIndex = siblings.indexOf(actor);
            const desktop = windows.filter(a => a !== firstWindow &&
                (a.get_meta_window().get_window_type() === Meta.WindowType.DESKTOP || a.get_meta_window().get_gtk_application_id?.() === 'com.rastersoft.ding')).at(-1);
            if (firstWindow && actorIndex > siblings.indexOf(firstWindow))
                global.window_group.set_child_below_sibling(actor, firstWindow);
            else if (desktop && actorIndex < siblings.indexOf(desktop))
                global.window_group.set_child_above_sibling(actor, desktop);
        }
    }
    destroy() {
        for (const [object, id] of this._signals) object.disconnect(id);
        this._signals = [];
        for (const {actor} of this._cards.values()) actor.destroy();
        this._cards.clear();
    }
}
