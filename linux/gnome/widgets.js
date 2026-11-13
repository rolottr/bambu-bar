// GNOME desktop cards. Live data comes from the app; never connect to printers from Shell.
import Clutter from 'gi://Clutter';
import Meta from 'gi://Meta';
import St from 'gi://St';
import Pango from 'gi://Pango';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {placeWidgets} from './widget-layout.js';

function label(text, style = '') {
    const l = new St.Label({text: text ?? '', style_class: style});
    l.clutter_text.ellipsize = Pango.EllipsizeMode.END;
    return l;
}
function column(style = '') { return new St.BoxLayout({orientation: Clutter.Orientation.VERTICAL, style_class: style}); }
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
        const small = widget.size === 'small';
        const large = widget.size === 'large';
        const content = column('bambu-widget-content');
        content.add_child(label(state.name || 'Bambu Bar', 'bambu-widget-name'));
        const caption = label(`● ${state.caption}`, 'bambu-widget-caption');
        caption.style = `color: ${/^#[0-9a-f]{6}$/i.test(state.stateColor) ? state.stateColor : '#adadad'};`;
        content.add_child(caption);
        content.add_child(label(state.headline, 'bambu-widget-job'));
        if (state.live) {
            content.add_child(label(`${state.progress}%`, 'bambu-widget-progress'));
            const rail = new St.Bin({style_class: 'bambu-widget-rail', x_expand: true});
            const fill = new St.Widget({style_class: 'bambu-widget-fill', x_align: Clutter.ActorAlign.START, height: 5});
            rail.set_child(fill);
            rail.connect('notify::width', () => { fill.width = rail.width * Math.max(0, Math.min(1, state.progress / 100)); });
            content.add_child(rail);
        }
        content.add_child(label(state.stateLine, 'bambu-widget-detail'));
        if (!small) {
            content.add_child(label(state.layers ?? state.detail, 'bambu-widget-detail'));
            content.add_child(label((state.temps ?? []).map(t => `${t.label} ${t.value}`).join('   ·   '), 'bambu-widget-detail'));
        }
        if (large) {
            if (state.issue) content.add_child(label(state.issue.text, 'bambu-widget-issue'));
            if (state.ams) content.add_child(label([state.ams.title, state.ams.humidity].filter(Boolean).join(' · '), 'bambu-widget-detail'));
            const spools = new St.BoxLayout({style_class: 'bambu-widget-spools'});
            for (const spool of state.spools ?? []) {
                const item = column('bambu-widget-spool'); item.x_expand = true;
                const dot = new St.Widget({style_class: 'bambu-widget-spool-ring', x_align: Clutter.ActorAlign.CENTER});
                const color = spool.color.replace('#', '').slice(0, 6);
                dot.style = `background-color: #${/^[0-9a-f]{6}$/i.test(color) ? color : '888888'};`;
                item.add_child(dot); item.add_child(label(spool.label, 'bambu-widget-detail')); item.add_child(label(spool.amount, 'bambu-widget-detail')); spools.add_child(item);
            }
            content.add_child(spools);
        }
        const actor = new St.Button({style_class: 'bambu-desktop-widget', can_focus: true, reactive: true, child: content,
            width: small ? 210 : 330, accessible_name: `${state.name}: ${state.caption}. Open printer.`});
        actor.connect('clicked', () => this._invoke('show', state.serial));
        return actor;
    }
    _layout() {
        if (Main.layoutManager.primaryIndex < 0) return;
        const area = Main.layoutManager.getWorkAreaForMonitor(Main.layoutManager.primaryIndex);
        if (!area) return;
        const cards = [...this._cards.values()];
        const positions = placeWidgets(cards.map(({actor, widget}) => {
            actor.width = Math.min(widget.size === 'small' ? 210 : 330, Math.max(160, area.width - 40));
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
