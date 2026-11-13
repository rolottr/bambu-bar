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
    constructor(invoke) {
        this._invoke = invoke;
        this._cards = new Map();
        this._signals = [];
        for (const [object, signal] of [[global.display, 'restacked'], [Main.layoutManager, 'monitors-changed'], [Main.overview, 'showing'], [Main.overview, 'hidden'], [Main.sessionMode, 'updated']])
            this._signals.push([object, object.connect(signal, () => this._layout())]);
    }
    update(widgets) {
        const keep = new Set(widgets.map(w => w.id));
        for (const [id, card] of this._cards) if (!keep.has(id)) { card.actor.destroy(); this._cards.delete(id); }
        for (const widget of widgets) {
            // SavedAt changes on every report and has no visual effect.
            const key = JSON.stringify({...widget, state: {...widget.state, savedAt: 0, updatedAt: 0}});
            if (this._cards.get(widget.id)?.key === key) continue;
            this._cards.get(widget.id)?.actor.destroy();
            const actor = this._build(widget);
            global.window_group.add_child(actor);
            this._cards.set(widget.id, {actor, key, widget});
        }
        this._layout();
    }
    _build(widget) {
        const state = widget.state;
        const model = widgetModel(state);
        const small = widget.size === 'small';
        const large = widget.size === 'large';
        const content = column('bambu-widget-content');
        content.x_expand = true;
        const header = new St.BoxLayout({style_class: 'bambu-widget-header'});
        const name = label(state.name || 'Bambu Bar', 'bambu-widget-name'); name.x_expand = true;
        header.add_child(name);
        const caption = label(`● ${state.caption}`, 'bambu-widget-caption');
        caption.style = `color: ${model.color};`;
        header.add_child(caption);
        const info = column('bambu-widget-info'); info.x_expand = true;
        if (small) {
            content.add_child(header);
            content.add_child(label(state.headline, 'bambu-widget-job'));
            const ring = new St.Widget({layout_manager: new Clutter.BinLayout(), x_align: Clutter.ActorAlign.CENTER, width: 84, height: 84});
            const arc = `<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><circle cx="50" cy="50" r="42" fill="none" stroke="#45484b" stroke-width="7"/><circle cx="50" cy="50" r="42" fill="none" stroke="${model.tint}" stroke-width="7" stroke-linecap="round" stroke-opacity="${model.progress > 0 ? 1 : 0}" stroke-dasharray="${model.progress * 2.639} 264" transform="rotate(-90 50 50)"/></svg>`;
            ring.add_child(new St.Icon({gicon: new Gio.BytesIcon({bytes: new GLib.Bytes(new TextEncoder().encode(arc))}), icon_size: 84}));
            ring.add_child(label(model.live ? `${model.progress}%` : '—', 'bambu-widget-progress small'));
            if (state.state === 'idle' && model.live) {
                content.add_child(label((state.temps ?? []).map(t => t.value).join('    '), 'bambu-widget-temperature'));
                content.add_child(spoolDots(state.spools));
            } else content.add_child(ring);
            const detail = label(state.stateLine, 'bambu-widget-detail'); detail.x_align = Clutter.ActorAlign.CENTER; content.add_child(detail);
            if (model.secondary) content.add_child(label(model.secondary, 'bambu-widget-detail'));
        } else {
            const hero = new St.BoxLayout({style_class: 'bambu-widget-hero', x_expand: true});
            const art = new St.Bin({style_class: 'bambu-widget-art', width: 104, height: 132, y_align: Clutter.ActorAlign.CENTER});
            if (state.art && GLib.path_is_absolute(state.art)) {
                const file = Gio.File.new_for_path(state.art);
                art.set_child(new St.Icon({gicon: new Gio.FileIcon({file}), icon_size: 128}));
            } else art.set_child(new St.Icon({icon_name: 'printer-symbolic', icon_size: 60, style_class: 'bambu-widget-detail'}));
            hero.add_child(art); info.add_child(header);
            info.add_child(label(state.headline, 'bambu-widget-job'));
            const numbers = new St.BoxLayout({style_class: 'bambu-widget-numbers'});
            const percent = label(model.live ? `${model.progress}%` : '—', 'bambu-widget-progress'); percent.x_expand = true; numbers.add_child(percent);
            numbers.add_child(label(state.layers ?? '', 'bambu-widget-detail'));
            if (state.state !== 'idle') info.add_child(numbers);
            const rail = new St.Bin({style_class: 'bambu-widget-rail', x_expand: true});
            const fill = new St.Widget({style_class: 'bambu-widget-fill', x_align: Clutter.ActorAlign.START, height: 5, style: `background-color: ${model.tint};`});
            rail.set_child(fill);
            rail.connect('notify::width', () => { fill.width = rail.width * model.progress / 100; });
            if (state.state === 'idle' && model.live) info.add_child(spoolDots(state.spools)); else info.add_child(rail);
            const detail = label(large ? model.detail : state.stateLine, 'bambu-widget-detail strong');
            if (detail.text) info.add_child(detail);
            if (state.finish) info.add_child(label((state.strings?.done || '{0}').replace('{0}', state.finish), 'bambu-widget-detail'));
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
            content.add_child(temperatures);
        }
        if (large) {
            const ams = column('bambu-widget-ams');
            if (state.ams) ams.add_child(label([state.ams.title, state.ams.humidity].filter(Boolean).join(' · '), 'bambu-widget-detail'));
            const spools = new St.BoxLayout({style_class: 'bambu-widget-spools'});
            for (const spool of state.spools ?? []) {
                const item = column('bambu-widget-spool'); item.x_expand = true;
                const rim = new St.Bin({style_class: 'bambu-widget-spool-rim', x_align: Clutter.ActorAlign.CENTER});
                if (spool.active) rim.style = 'border-color: #21cc6b;';
                const color = spool.color.replace('#', '').slice(0, 6);
                const disc = new St.Bin({style_class: 'bambu-widget-spool-ring', style: `background-color: #${/^[0-9a-f]{6}$/i.test(color) ? color : '888888'};`});
                disc.set_child(new St.Widget({style_class: 'bambu-widget-spool-hub', x_align: Clutter.ActorAlign.CENTER, y_align: Clutter.ActorAlign.CENTER})); rim.set_child(disc);
                item.add_child(rim); item.add_child(label(spool.name || spool.label, 'bambu-widget-detail strong')); item.add_child(label(spool.amount, 'bambu-widget-detail')); spools.add_child(item);
            }
            ams.add_child(spools); content.add_child(ams);
            if (model.issue) {
                const issue = label(`⚠ ${model.issue}`, 'bambu-widget-issue');
                issue.clutter_text.line_wrap = true; content.add_child(issue);
            }
            if (state.lastJob) content.add_child(label(`${state.lastJob.name} · ${state.lastJob.ago}`, 'bambu-widget-detail'));
        }
        const [width, height] = model.dimensions[widget.size] ?? model.dimensions.medium;
        const actor = new St.Button({style_class: `bambu-desktop-widget ${widget.size}`, can_focus: true, reactive: true, child: content,
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
            actor.width = Math.min(widget.size === 'small' ? 180 : 380, Math.max(160, area.width - 40));
            return {width: actor.width, height: actor.get_preferred_height(actor.width)[1], corner: widget.corner};
        }), area);
        // Desktop icon surfaces must stay below the cards; ordinary windows stay above them.
        const windows = global.window_group.get_children().filter(a => a.get_meta_window?.());
        const firstWindow = windows.find(a => {
            const w = a.get_meta_window();
            return w.get_window_type() !== Meta.WindowType.DESKTOP && w.get_gtk_application_id?.() !== 'com.rastersoft.ding';
        });
        for (const [index, {actor}] of cards.entries()) {
            actor.visible = !Main.overview.visible && !Main.sessionMode.isLocked;
            actor.set_position(positions[index].x, positions[index].y);
            if (firstWindow) global.window_group.set_child_below_sibling(actor, firstWindow);
            else global.window_group.set_child_above_sibling(actor, null);
        }
    }
    destroy() {
        for (const [object, id] of this._signals) object.disconnect(id);
        this._signals = [];
        for (const {actor} of this._cards.values()) actor.destroy();
        this._cards.clear();
    }
}
