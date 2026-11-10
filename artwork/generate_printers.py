"""Bambu Bar's printer models, built in Blender.

Run headless from the repository root:
    Blender -b --factory-startup -P artwork/generate_printers.py -- [enclosed wide openFrame bedSlinger mini]
or inside a running Blender (the MCP add-on) with `exec` and `ARGS = [...]` set beforehand.

All geometry and materials are authored here; no product photographs, CAD or logos are used. The shapes are
visual approximations of Bambu Lab's printer families at their published outer dimensions:
    enclosed    P2S, P1S, X1 family, X2D   389 x 389 x 457 mm, AMS on top
    wide        H2 family                  492 x 514 x 626 mm (body ~ 550 tall), AMS 2 Pro on top
    h3c         H3C                        493 x 571 x 587 mm body, nine-tool rack, the AMS 3H built into a hood on
                                           top (about 1 m tall in all), nine spools side by side under a clear lid
    openFrame   P1P                        386 x 389 x 458 mm, AMS on top
    bedSlinger  A1, A2L                    385 x 410 x 445 mm, AMS Lite on top
    mini        A1 mini                    312 x 296 x 365 mm, AMS Lite on top

For every shape it writes, under shared/Printers:
    <shape>.usdz            the model for the app's interactive view. Nodes the app drives by name:
                            Part_00…Part_NN (print slices, bottom up), Filament_i / Reel_i (spools), Lamp,
                            Toolhead, BedCarriage (CoreXY: bed and print move down together), Gantry (bed slingers:
                            the X beam and toolhead rise), Bed (bed slingers: moves front to back), Door.
    <shape>-body.png        everything but the print and spools, chamber light on
    <shape>-dark.png        the same with the chamber light off
    <shape>-part.png        the finished print in white, cut out by whatever stands in front of it
    <shape>-spool<i>.png    spool i in white (tinted by the app): four, or nine for the H3C's AMS 3H
    <shape>-sheen.png       reflections on the door glass, drawn over the print
    <shape>.json            where the print sits in the frame, for revealing it with progress
"""
import bpy
import bmesh
import json
import math
import sys
from mathutils import Vector
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent if '__file__' in globals() else Path(globals().get('ROOT_DIR', '.'))
OUT = ROOT / 'shared/Printers'
SLICES = 32
RES = (480, 576)  # 100 x 120 like the vector drawing

# --------------------------------------------------------------------------- scene


def fresh_scene(name):
    """A new scene in the current file, so a running Blender's own scenes are left alone. Earlier printer scenes
    go first: object names are global, and the app finds nodes by exact name."""
    olds = [s for s in bpy.data.scenes if s.name.startswith('Bambu Bar ')]
    doomed = {ob for s in olds for ob in s.objects}
    doomed |= {ob for c in bpy.data.collections if c.name.startswith('BB ') for ob in c.all_objects}
    for ob in doomed:
        bpy.data.objects.remove(ob, do_unlink=True)
    for coll in [c for c in bpy.data.collections if c.name.startswith('BB ')]:
        bpy.data.collections.remove(coll)
    for old in olds:
        if bpy.context.window and bpy.context.window.scene == old:
            others = [s for s in bpy.data.scenes if not s.name.startswith('Bambu Bar ')]
            if others:
                bpy.context.window.scene = others[0]
        bpy.data.scenes.remove(old)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.lights, bpy.data.cameras, bpy.data.worlds):
        for item in list(block):
            if item.users == 0:
                block.remove(item)
    scene = bpy.data.scenes.new(name)
    scene.unit_settings.system = 'METRIC'
    if bpy.context.window:
        bpy.context.window.scene = scene
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.lights, bpy.data.cameras):
        for item in list(block):
            if item.users == 0:
                block.remove(item)
    return scene


class Kit:
    def __init__(self, scene, shape):
        self.scene = scene
        self.shape = shape
        self.model = bpy.data.collections.new('BB ' + shape)
        scene.collection.children.link(self.model)
        self.studio = bpy.data.collections.new('BB ' + shape + ' studio')
        scene.collection.children.link(self.studio)
        self.mats = {}
        self.glass = []      # door and lid glass: hidden from the print and spool layers
        self.door = []       # what the sheen layer reflects in
        self.parts = []
        self.spools = []     # [(reel objects, filament object)]

    # ---- materials

    def mat(self, name, color, rough=.5, metal=0., coat=0., emit=None, strength=0., transmission=0., alpha=1.,
            noise=0., noise_scale=800):
        key = name
        if key in self.mats:
            return self.mats[key]
        m = bpy.data.materials.new('BB_' + name)
        m.use_nodes = True
        m.diffuse_color = (*color, alpha)
        p = m.node_tree.nodes['Principled BSDF']
        p.inputs['Base Color'].default_value = (*color, 1)
        p.inputs['Roughness'].default_value = rough
        p.inputs['Metallic'].default_value = metal
        p.inputs['Coat Weight'].default_value = coat
        p.inputs['Transmission Weight'].default_value = transmission
        p.inputs['Alpha'].default_value = alpha
        if emit:
            p.inputs['Emission Color'].default_value = (*emit, 1)
            p.inputs['Emission Strength'].default_value = strength
        if noise:
            tex = m.node_tree.nodes.new('ShaderNodeTexNoise')
            tex.inputs['Scale'].default_value = noise_scale
            tex.inputs['Detail'].default_value = 3
            bump = m.node_tree.nodes.new('ShaderNodeBump')
            bump.inputs['Strength'].default_value = noise
            bump.inputs['Distance'].default_value = .0002
            m.node_tree.links.new(tex.outputs['Fac'], bump.inputs['Height'])
            m.node_tree.links.new(bump.outputs['Normal'], p.inputs['Normal'])
        self.mats[key] = m
        return m

    # ---- objects

    def link(self, ob, parent=None, mat=None):
        self.model.objects.link(ob)
        if mat is not None and ob.data is not None:
            ob.data.materials.append(mat)
        if parent is not None:
            ob.parent = parent
        return ob

    def empty(self, name, loc=(0, 0, 0), parent=None):
        ob = bpy.data.objects.new(name, None)
        ob.location = loc
        ob.empty_display_size = .02
        return self.link(ob, parent)

    def mesh_from_bm(self, name, bm, mat, loc=(0, 0, 0), parent=None, smooth=False):
        data = bpy.data.meshes.new(name)
        bm.normal_update()
        bm.to_mesh(data)
        bm.free()
        for poly in data.polygons:
            poly.use_smooth = smooth
        ob = bpy.data.objects.new(name, data)
        ob.location = loc
        return self.link(ob, parent, mat)

    def box(self, name, size, loc, mat, bevel=0., segments=4, parent=None):
        bm = bmesh.new()
        bmesh.ops.create_cube(bm, size=1)
        for v in bm.verts:
            v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
        ob = self.mesh_from_bm(name, bm, mat, loc, parent)
        if bevel:
            self.bevel(ob, bevel, segments)
        return ob

    def bevel(self, ob, width, segments=4):
        mod = ob.modifiers.new('Bevel', 'BEVEL')
        mod.width = width
        mod.segments = segments
        mod.limit_method = 'ANGLE'
        mod.harden_normals = False
        for poly in ob.data.polygons:
            poly.use_smooth = True
        ob.data.set_sharp_from_angle(angle=math.radians(40))
        return mod

    def cut(self, ob, cutter_size, cutter_loc, bevel=0.):
        """Boolean difference with a box; the cutter is a hidden helper."""
        cutter = self.box(ob.name + '_cut', cutter_size, cutter_loc, None, bevel=bevel)
        cutter.hide_render = True
        cutter.hide_viewport = True
        cutter.display_type = 'WIRE'
        self.model.objects.unlink(cutter)
        self.scene.collection.objects.link(cutter)
        cutter.hide_set(True)
        mod = ob.modifiers.new('Cut', 'BOOLEAN')
        mod.operation = 'DIFFERENCE'
        mod.object = cutter
        mod.solver = 'EXACT'
        return cutter

    def cylinder(self, name, radius, depth, loc, mat, axis='Z', segments=48, parent=None, smooth=True, cap=True):
        bm = bmesh.new()
        bmesh.ops.create_cone(bm, cap_ends=cap, cap_tris=False, segments=segments, radius1=radius, radius2=radius,
                              depth=depth)
        rot = {'X': (0, math.pi / 2, 0), 'Y': (math.pi / 2, 0, 0), 'Z': (0, 0, 0)}[axis]
        ob = self.mesh_from_bm(name, bm, mat, loc, parent, smooth=smooth)
        ob.rotation_euler = rot
        # Flat caps, round sides.
        ob.data.set_sharp_from_angle(angle=math.radians(50))
        return ob

    def tube(self, name, r_out, r_in, depth, loc, mat, axis='X', segments=64, parent=None):
        """A ring (spool flange, filament winding) with inner and outer walls."""
        bm = bmesh.new()
        rings = []
        for z, r in ((-depth / 2, r_out), (depth / 2, r_out), (depth / 2, r_in), (-depth / 2, r_in)):
            ring = [bm.verts.new((r * math.cos(2 * math.pi * i / segments), r * math.sin(2 * math.pi * i / segments), z))
                    for i in range(segments)]
            rings.append(ring)
        for a, b in ((0, 1), (1, 2), (2, 3), (3, 0)):
            for i in range(segments):
                j = (i + 1) % segments
                bm.faces.new((rings[a][i], rings[a][j], rings[b][j], rings[b][i]))
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        rot = {'X': (0, math.pi / 2, 0), 'Y': (math.pi / 2, 0, 0), 'Z': (0, 0, 0)}[axis]
        ob = self.mesh_from_bm(name, bm, mat, loc, parent, smooth=True)
        ob.rotation_euler = rot
        ob.data.set_sharp_from_angle(angle=math.radians(50))
        return ob

    def arc(self, name, x, centre, radius, a0, a1, r, mat, parent=None, steps=18, segments=10):
        """A thin tube bent along an arc in the YZ plane at `x` (feed tubes), from angle a0 to a1 in degrees."""
        bm = bmesh.new()
        rings = []
        for k in range(steps + 1):
            a = math.radians(a0 + (a1 - a0) * k / steps)
            p = Vector((x, centre[0] + radius * math.cos(a), centre[1] + radius * math.sin(a)))
            v = Vector((0, math.cos(a), math.sin(a)))
            rings.append([bm.verts.new(p + r * (math.cos(2 * math.pi * i / segments) * Vector((1, 0, 0)) +
                                                math.sin(2 * math.pi * i / segments) * v)) for i in range(segments)])
        for a, b in zip(rings, rings[1:]):
            for i in range(segments):
                j = (i + 1) % segments
                bm.faces.new((a[i], a[j], b[j], b[i]))
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        return self.mesh_from_bm(name, bm, mat, (0, 0, 0), parent, smooth=True)

    def lathe_slice(self, name, profile, z0, z1, mat, parent, segments=56):
        """One closed band of a lathed solid between heights z0 and z1 (profile: [(z, r)])."""
        def radius(z):
            for (za, ra), (zb, rb) in zip(profile, profile[1:]):
                if za <= z <= zb:
                    t = (z - za) / (zb - za) if zb > za else 0
                    t = t * t * (3 - 2 * t)
                    return ra + (rb - ra) * t
            return profile[-1][1]
        steps = 3
        zs = [z0 + (z1 - z0) * k / steps for k in range(steps + 1)]
        bm = bmesh.new()
        rings = []
        for z in zs:
            r = radius(z)
            rings.append([bm.verts.new((r * math.cos(2 * math.pi * i / segments), r * math.sin(2 * math.pi * i / segments), z))
                          for i in range(segments)])
        for a, b in zip(rings, rings[1:]):
            for i in range(segments):
                j = (i + 1) % segments
                bm.faces.new((a[i], a[j], b[j], b[i]))
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[-1])
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        ob = self.mesh_from_bm(name, bm, mat, (0, 0, 0), parent, smooth=True)
        ob.data.set_sharp_from_angle(angle=math.radians(60))
        return ob


# --------------------------------------------------------------------------- shared parts


def materials(kit):
    m = {}
    m['shell'] = kit.mat('Shell', (.035, .037, .041), rough=.34, coat=.35, noise=.03)
    m['shell_light'] = kit.mat('ShellLight', (.62, .63, .65), rough=.38, coat=.2)
    m['frame'] = kit.mat('Frame', (.018, .019, .021), rough=.3, coat=.3)
    m['inner'] = kit.mat('Inner', (.035, .037, .04), rough=.75)
    m['metal'] = kit.mat('Metal', (.62, .63, .66), rough=.22, metal=1)
    m['carbon'] = kit.mat('Carbon', (.02, .02, .022), rough=.25, coat=.6)
    m['plate'] = kit.mat('Plate', (.42, .32, .16), rough=.5, metal=.7, noise=.8, noise_scale=2500)
    m['bedbase'] = kit.mat('BedBase', (.06, .06, .065), rough=.5, metal=.4)
    m['toolhead'] = kit.mat('Toolhead', (.08, .085, .09), rough=.35, coat=.4)
    m['toolface'] = kit.mat('ToolFace', (.55, .56, .58), rough=.3, metal=.2, coat=.4)
    m['nozzle'] = kit.mat('Nozzle', (.8, .55, .25), rough=.25, metal=1)
    m['glass'] = kit.mat('Glass', (.32, .34, .36), rough=.02, transmission=1, alpha=1)
    m['smoke'] = kit.mat('Smoke', (.16, .16, .17), rough=.03, transmission=1, alpha=1)
    m['screen'] = kit.mat('Screen', (.006, .006, .008), rough=.05, coat=1, emit=(.2, .45, .9), strength=.04)
    m['led'] = kit.mat('Led', (.1, .9, .45), rough=.3, emit=(.13, .85, .42), strength=4)
    m['lamp'] = kit.mat('Lamp', (.95, .93, .88), rough=.3, emit=(1, .95, .86), strength=12)
    m['part'] = kit.mat('Part', (.86, .86, .86), rough=.42)
    m['filament'] = kit.mat('Filament', (.86, .86, .86), rough=.35, noise=.25, noise_scale=1800)
    m['reel'] = kit.mat('Reel', (.025, .025, .028), rough=.45)
    m['rubber'] = kit.mat('Rubber', (.015, .015, .016), rough=.8)
    m['ams'] = kit.mat('AMS', (.05, .052, .056), rough=.4, coat=.2, noise=.03)
    m['ams_light'] = kit.mat('AMSLight', (.6, .61, .63), rough=.38, coat=.2)
    m['tube'] = kit.mat('Tube', (.85, .86, .88), rough=.25, coat=.4)
    m['alu'] = kit.mat('Alu', (.4, .41, .43), rough=.42, metal=.45, noise=.04)
    return m


def vase_profile(height, scale):
    return [(0, .030 * scale), (height * .12, .040 * scale), (height * .45, .052 * scale),
            (height * .78, .042 * scale), (height, .033 * scale)]


def print_slices(kit, m, parent, base_z, height, scale):
    profile = vase_profile(height, scale)
    for i in range(SLICES):
        z0, z1 = height * i / SLICES, height * (i + 1) / SLICES
        ob = kit.lathe_slice('Part_%02d' % i, profile, z0, z1, m['part'], parent)
        ob.location.z = base_z
        kit.parts.append(ob)
    return profile


def build_plate(kit, m, parent, size, z, thick=.006):
    bed = kit.box('BedFrame', (size + .02, size + .02, .012), (0, 0, z - .009), m['bedbase'], bevel=.003, parent=parent)
    plate = kit.box('Plate', (size, size, thick), (0, 0, z - thick / 2), m['plate'], bevel=.002, parent=parent)
    # Locating tab at the back.
    kit.box('PlateTab', (.05, .012, thick), (0, size / 2 + .004, z - thick / 2), m['plate'], bevel=.002, parent=parent)
    return bed, plate


def toolhead(kit, m, parent, loc, width=.062, depth=.06, height=.072, dual=False):
    head = kit.empty('Toolhead', loc, parent)
    kit.box('ToolBody', (width, depth, height), (0, 0, 0), m['toolhead'], bevel=.008, parent=head)
    kit.box('ToolFace', (width * .82, .004, height * .62), (0, -depth / 2 - .001, .006), m['toolface'], bevel=.003, parent=head)
    # Cutter lever and the status light strip on the front.
    kit.box('ToolLever', (.006, .01, .03), (width / 2 - .004, -depth / 2 - .004, .012), m['frame'], bevel=.002, parent=head)
    kit.box('ToolLed', (width * .4, .002, .003), (0, -depth / 2 - .0035, -height * .28), m['led'], parent=head)
    tips = (-.016, .016) if dual else (0,)
    for i, x in enumerate(tips):
        kit.cylinder('Heatsink%d' % i, .007, .012, (x, 0, -height / 2 - .006), m['metal'], segments=24, parent=head)
        nozzle_tip(kit, m, 'NozzleTip%d' % i, (x, 0, -height / 2 - .016), head)
    return head


def nozzle_tip(kit, m, name, loc, parent=None):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=24, radius1=.0012, radius2=.0045, depth=.008)
    return kit.mesh_from_bm(name, bm, m['nozzle'], loc, parent)


def tool_rack(kit, m, span, y, z, count=9):
    """The H3C's docked hotends in a row along the top front of the chamber, each with its status light."""
    kit.box('RackBar', (span + .02, .032, .016), (0, y, z), m['frame'], bevel=.004)
    step = span / count
    for i in range(count):
        x = -span / 2 + step * (i + .5)
        kit.box('Dock_%d' % i, (step * .72, .03, .03), (x, y - .002, z - .022), m['toolhead'], bevel=.004)
        kit.box('DockLed_%d' % i, (step * .28, .002, .003), (x, y - .018, z - .012), m['led'])
        kit.cylinder('DockHeatsink_%d' % i, .0055, .016, (x, y, z - .045), m['metal'], segments=20)
        nozzle_tip(kit, m, 'DockNozzle_%d' % i, (x, y, z - .057))


def ams(kit, m, base_z, width=.36, depth=.285, height=.228, light=False, name='AMS'):
    """The AMS: an opaque tub with a smoked lid; four spools stand side by side, axles left to right."""
    root = kit.empty(name, (0, 0, base_z))
    body = m['ams_light'] if light else m['ams']
    tub_h = height * .58
    tub = kit.box('AMSTub', (width, depth, tub_h), (0, 0, tub_h / 2), body, bevel=.012, parent=root)
    kit.cut(tub, (width - .016, depth - .016, tub_h), (0, 0, base_z + tub_h / 2 + .008))
    lid_h = height - tub_h
    lid = kit.box('AMSLid', (width - .002, depth - .002, lid_h), (0, 0, tub_h + lid_h / 2), m['smoke'], bevel=.016, parent=root)
    kit.cut(lid, (width - .01, depth - .01, lid_h), (0, 0, base_z + tub_h + lid_h / 2 - .005))
    kit.glass.append(lid)
    # Feet line and front latch.
    kit.box('AMSBand', (width + .001, depth + .001, .006), (0, 0, tub_h - .003), m['frame'], bevel=.002, parent=root)
    kit.box('AMSLatch', (.07, .006, .012), (0, -depth / 2 - .002, tub_h - .014), m['frame'], bevel=.003, parent=root)
    # Spools.
    slot = (width - .04) / 4
    for i in range(4):
        x = -width / 2 + .02 + slot * (i + .5)
        spool = kit.empty('Spool_%d' % i, (x, 0, .112), root)
        reel = []
        for side in (-1, 1):
            reel.append(kit.tube('Flange_%d_%s' % (i, 'L' if side < 0 else 'R'), .1, .028, .0035,
                                 (side * (.0315), 0, 0), m['reel'], axis='X', parent=spool))
        reel.append(kit.tube('Core_%d' % i, .042, .028, .06, (0, 0, 0), m['reel'], axis='X', parent=spool))
        fil = kit.tube('Filament_%d' % i, .088, .041, .058, (0, 0, 0), m['filament'], axis='X', parent=spool)
        reel_group = reel
        kit.spools.append((reel_group, fil, spool))
    return root


def ams_lite(kit, m, base_z, span=.34):
    """The AMS Lite's four spools in a row on a slim rail, faces to the front."""
    root = kit.empty('AMS', (0, 0, base_z))
    kit.box('LiteRail', (span + .03, .03, .014), (0, .02, .007), m['shell'], bevel=.005, parent=root)
    step = span / 4
    for i in range(4):
        x = -span / 2 + step * (i + .5)
        kit.box('LiteArm_%d' % i, (.012, .012, .03), (x, .02, .02), m['shell'], bevel=.004, parent=root)
        hub = kit.cylinder('Hub_%d' % i, .011, .05, (x, 0, .057), m['shell'], axis='Y', segments=32, parent=root)
        hub.rotation_euler.z = math.radians(-55)
        spool = kit.empty('Spool_%d' % i, (x, 0, .014 + .043), root)
        # Turned so the filament band shows beside the flange from the camera's side.
        spool.rotation_euler.z = math.radians(-55)
        reel = [kit.tube('Flange_%d_F' % i, .042, .012, .0025, (0, -.017, 0), m['reel'], axis='Y', parent=spool),
                kit.tube('Flange_%d_B' % i, .042, .012, .0025, (0, .017, 0), m['reel'], axis='Y', parent=spool),
                kit.tube('Core_%d' % i, .017, .012, .034, (0, 0, 0), m['reel'], axis='Y', parent=spool)]
        fil = kit.tube('Filament_%d' % i, .038, .016, .031, (0, 0, 0), m['filament'], axis='Y', parent=spool)
        kit.spools.append((reel, fil, spool))
    return root


def ams_3h(kit, m, base_z, W, D):
    """The H3C's AMS 3H, built into a dark hood on the printer: a smoked window over the nine feed tubes, then a
    clear rounded lid over nine spools standing side by side, axles left to right. The hood is part of the printer,
    so the render without an AMS keeps it and only drops the spools."""
    hood = kit.empty('Hood', (0, 0, base_z))
    neck_h, cap_h, band_h = .2, .215, .095
    nw, nd = W - .014, D - .03
    neck = kit.box('HoodNeck', (nw, nd, neck_h + .02), (0, .01, neck_h / 2 - .01), m['shell'], bevel=.012, parent=hood)
    # Open at the front behind the window, so the tubes show.
    kit.cut(neck, (nw - .03, nd, neck_h - .04), (0, .01 - .012, base_z + neck_h / 2 + .005), bevel=.006)
    window = kit.box('HoodWindow', (nw - .02, .004, neck_h - .03), (0, .01 - nd / 2 - .001, neck_h / 2 + .005), m['smoke'],
                     bevel=.006, parent=hood)
    kit.glass.append(window)
    span = W - .06
    for i in range(9):
        x = -span / 2 + span / 9 * (i + .5)
        kit.arc('FeedTube_%d' % i, x, (.01 - nd / 2 + .1, neck_h / 2 + .005), neck_h * .36, 95, 265, .0026, m['tube'], hood)
    # The lid: a smoked shell with big rounded top edges, its lower part behind an opaque band.
    cap = kit.box('HoodLid', (W - .004, D - .004, cap_h), (0, -.002, neck_h + cap_h / 2), m['smoke'], bevel=.08, segments=10,
                  parent=hood)
    kit.cut(cap, (W - .02, D - .02, cap_h), (0, -.002, base_z + neck_h + cap_h / 2 - .008), bevel=.07)
    kit.glass.append(cap)
    kit.box('HoodBand', (W + .002, D + .002, band_h), (0, -.002, neck_h + band_h / 2), m['shell'], bevel=.014, parent=hood)
    kit.box('HoodSeam', (W + .003, D + .003, .004), (0, -.002, neck_h + band_h - .006), m['frame'], bevel=.0015, parent=hood)
    kit.box('HoodLatch', (.09, .006, .012), (0, -.002 - D / 2 - .002, neck_h + band_h - .02), m['frame'], bevel=.003, parent=hood)
    kit.box('HoodFloor', (W - .03, D - .03, .006), (0, -.002, neck_h + .02), m['inner'], parent=hood)
    # Spools, toward the front of the lid; the dryer trays behind them aren't drawn.
    root = kit.empty('AMS', (0, 0, base_z))
    pitch = (W - .03) / 9
    for i in range(9):
        x = -(W - .03) / 2 + pitch * (i + .5)
        spool = kit.empty('Spool_%d' % i, (x, -D / 2 + .15, neck_h + .108), root)
        reel = [kit.tube('Flange_%d_%s' % (i, side), .086, .024, .003, (sx * .0225, 0, 0), m['reel'], axis='X', parent=spool)
                for side, sx in (('L', -1), ('R', 1))]
        reel.append(kit.tube('Core_%d' % i, .036, .024, .042, (0, 0, 0), m['reel'], axis='X', parent=spool))
        fil = kit.tube('Filament_%d' % i, .077, .035, .041, (0, 0, 0), m['filament'], axis='X', parent=spool)
        kit.spools.append((reel, fil, spool))
    return root


# --------------------------------------------------------------------------- printer families


def corexy(kit, m, W, D, H, open_frame=False, dual=False, light_shell=False, base_h=.085, screen_top=False, rack=0,
           shell=None):
    """A CoreXY box: shell (or frame), glass door, chamber light, gantry, toolhead, bed and print. `screen_top` puts
    the screen on a stalk above the door's top left (H3C); `rack` docks that many hotends along the top front."""
    shell_mat = shell or (m['shell_light'] if light_shell else m['shell'])
    top_h, wall = .055, .022
    cav_w, cav_d = W - 2 * wall, D - 2 * wall
    cav_z0, cav_z1 = base_h, H - top_h

    if open_frame:
        # Four posts, a top ring and a base: the P1P's bare frame.
        post = .03
        for sx in (-1, 1):
            for sy in (-1, 1):
                kit.box('Post', (post, post, H), (sx * (W / 2 - post / 2), sy * (D / 2 - post / 2), H / 2), m['frame'], bevel=.006)
        ring = kit.box('TopRing', (W, D, top_h * .6), (0, 0, H - top_h * .3), m['frame'], bevel=.008)
        kit.cut(ring, (W - 2 * post, D - 2 * post, top_h), (0, 0, H - top_h * .3))
        kit.box('Base', (W, D, base_h), (0, 0, base_h / 2), shell_mat, bevel=.012)
        kit.box('BackPanel', (W - 2 * post, .004, H - base_h - top_h * .6), (0, D / 2 - .01, (base_h + H - top_h * .6) / 2), m['inner'])
    else:
        shell = kit.box('Shell', (W, D, H), (0, 0, H / 2), shell_mat, bevel=.022, segments=6)
        # The chamber, open at the front where the door sits.
        kit.cut(shell, (cav_w, cav_d + wall * 2, cav_z1 - cav_z0), (0, -wall, (cav_z0 + cav_z1) / 2), bevel=.008)
        # Dark chamber lining so the inside doesn't read as the shell colour.
        lining = kit.box('Lining', (cav_w - .002, cav_d - .002, cav_z1 - cav_z0 - .002), (0, 0, (cav_z0 + cav_z1) / 2), m['inner'])
        kit.cut(lining, (cav_w - .006, cav_d + .02, cav_z1 - cav_z0 - .006), (0, -.004, (cav_z0 + cav_z1) / 2))
        # Door: glass pane with a thin dark frame and a handle at the right edge.
        door = kit.empty('Door', (W / 2 - wall, -D / 2, 0))  # hinged on the right
        dw, dh = W - .018, cav_z1 - cav_z0 + .03
        pane = kit.box('DoorGlass', (dw - .006, .005, dh - .006), (-(dw / 2) - .006, -.003, (cav_z0 + cav_z1) / 2), m['glass'],
                       bevel=.004, parent=door)
        kit.glass.append(pane)
        kit.door.append(pane)
        frame = kit.box('DoorFrame', (dw, .006, dh), (-(dw / 2) - .006, -.0035, (cav_z0 + cav_z1) / 2), m['frame'], bevel=.006,
                        parent=door)
        kit.cut(frame, (dw - .024, .02, dh - .024), (W / 2 - wall - dw / 2 - .006, -D / 2 - .0035, (cav_z0 + cav_z1) / 2), bevel=.006)
        kit.box('DoorHandle', (.008, .012, dh * .32), (-.016, -.012, (cav_z0 + cav_z1) / 2), m['frame'], bevel=.003, parent=door)
        if screen_top:
            # On a short stalk at the top left, standing proud of the door.
            kit.box('ScreenStalk', (.016, .02, .02), (-W / 2 + .085, -D / 2 - .008, H - top_h / 2), m['frame'], bevel=.004)
            kit.box('ScreenBezel', (.112, .008, .07), (-W / 2 + .1, -D / 2 - .019, H - .018), m['frame'], bevel=.006)
            kit.box('Screen', (.1, .004, .058), (-W / 2 + .1, -D / 2 - .0235, H - .018), m['screen'], bevel=.004)
            # Dark filter windows high on the side panels.
            for sx in (-1, 1):
                kit.box('SideWindow', (.003, D * .4, H * .17), (sx * (W / 2 + .0008), -D * .06, H * .78), m['frame'], bevel=.006)
        else:
            # Front fascia under the door: screen on the left, light bar in the middle.
            kit.box('Screen', (.085, .004, .05), (-W / 2 + .075, -D / 2 - .0005, base_h * .5), m['screen'], bevel=.004)
            kit.box('StatusBar', (.06, .003, .004), (0, -D / 2 - .0005, base_h * .42), m['led'], bevel=.0015)
        # Feet.
        for sx in (-1, 1):
            for sy in (-1, 1):
                kit.cylinder('Foot', .016, .008, (sx * (W / 2 - .04), sy * (D / 2 - .04), -.003), m['rubber'], segments=24)

    # Chamber light: a short strip in the top left corner, facing into the chamber.
    lamp = kit.box('Lamp', (cav_w * .45, .012, .002), (-cav_w * .18, -cav_d / 2 + .03, cav_z1 - .002), m['lamp'])
    # The renders show the light it casts, not a hot spot (it would also mirror in the door).
    lamp.visible_camera = False
    lamp.visible_glossy = False
    lamp_point = Vector((-cav_w * .18, -cav_d / 2 + .06, cav_z1 - .03))
    if not open_frame:
        # Vent slots low on the side panels, toward the back.
        for sx in (-1, 1):
            for k in range(5):
                kit.box('Vent', (.002, .07, .004), (sx * (W / 2 + .0006), D / 2 - .09, .03 + k * .011), m['frame'], bevel=.0008)

    # Gantry: two Y rails along the sides, one carbon X rod across, the toolhead on it.
    gz = cav_z1 - .045
    for sx in (-1, 1):
        kit.box('YRail', (.018, cav_d - .02, .02), (sx * (cav_w / 2 - .012), 0, gz), m['metal'], bevel=.003)
    kit.cylinder('XRod', .0055, cav_w - .04, (0, -.03, gz), m['carbon'], axis='X', segments=24)
    kit.cylinder('XRod2', .0055, cav_w - .04, (0, .0, gz), m['carbon'], axis='X', segments=24)
    head = toolhead(kit, m, None, (cav_w * .08, -.02, gz - .006), width=.085 if dual else .062, dual=dual)
    if rack:
        tool_rack(kit, m, cav_w - .08, -cav_d / 2 + .035, cav_z1 - .014, rack)

    # Bed and print. At 100 % the bed has dropped by the print's height; the model is posed there.
    part_h = (cav_z1 - cav_z0) * .38
    nozzle_z = gz - .006 - .036 - .02
    plate_size = min(cav_w, cav_d) * .74
    carriage = kit.empty('BedCarriage', (0, .004, nozzle_z - part_h))
    build_plate(kit, m, carriage, plate_size, 0)
    print_slices(kit, m, carriage, 0, part_h, plate_size / .25)
    # Z lead screws at the back corners and the front.
    for x, y in ((-plate_size / 2 - .012, plate_size / 2 - .02), (plate_size / 2 + .012, plate_size / 2 - .02),
                 (0, plate_size / 2 + .03)):
        kit.cylinder('ZScrew', .004, cav_z1 - cav_z0 - .01, (x, y, (cav_z0 + cav_z1) / 2), m['metal'], segments=16)
    # Purge chute and cutter bracket at the back left.
    kit.box('Chute', (.03, .03, .05), (-cav_w / 2 + .03, cav_d / 2 - .03, cav_z0 + .03), m['frame'], bevel=.004)
    return dict(part_h=part_h, nozzle_z=nozzle_z, lamp=lamp_point, top=H, carriage=carriage, head=head,
                travel=(nozzle_z - part_h, nozzle_z))


def bedslinger(kit, m, mini=False):
    """A1 / A1 mini: base with the Y bed, an arch (or single column), the X beam with the toolhead."""
    if mini:
        W, D, H, base_h, bed = .312, .296, .365, .055, .18
    else:
        W, D, H, base_h, bed = .385, .41, .445, .06, .256
    base = kit.box('Base', (W - .02, D, base_h), (0, 0, base_h / 2), m['shell_light'], bevel=.014, segments=5)
    kit.box('BaseTrim', (W - .019, D + .001, .008), (0, 0, base_h - .006), m['shell'], bevel=.003)
    kit.box('Screen', (.07 if mini else .08, .004, .034), (W / 2 - .07, -D / 2 - .0006, base_h * .48), m['screen'], bevel=.004)
    kit.box('YRailCover', (.06, D - .03, .008), (0, 0, base_h + .004), m['shell'], bevel=.003)
    for sx in (-1, 1):
        for sy in (-1, 1):
            kit.cylinder('Foot', .014, .008, (sx * (W / 2 - .05), sy * (D / 2 - .05), -.003), m['rubber'], segments=24)
    # The Y bed rides on the base; the app slides it front to back while printing.
    bed_z = base_h + .045
    bed_root = kit.empty('Bed', (0, 0, bed_z))
    build_plate(kit, m, bed_root, bed, 0)
    kit.box('BedHeater', (bed * .9, bed * .9, .02), (0, 0, -.02), m['bedbase'], bevel=.004, parent=bed_root)
    part_h = (H - bed_z) * .36
    print_slices(kit, m, bed_root, 0, part_h, bed / .25 * (.95 if mini else 1))

    # Arch (A1) or single Z column (A1 mini).
    col_w = .045
    if mini:
        kit.box('Column', (col_w, .05, H - base_h - .015), (-W / 2 + col_w / 2 + .01, .06, base_h + (H - base_h - .015) / 2), m['shell_light'], bevel=.01)
        kit.box('ColumnCap', (col_w + .004, .054, .02), (-W / 2 + col_w / 2 + .01, .06, H - .01), m['shell'], bevel=.006)
    else:
        for sx in (-1, 1):
            kit.box('Tower', (col_w, .05, H - base_h - .02), (sx * (W / 2 - col_w / 2), .05, base_h + (H - base_h - .02) / 2), m['shell_light'], bevel=.01)
        kit.box('TopBar', (W, .05, .035), (0, .05, H - .0175), m['shell_light'], bevel=.01)

    # The X beam and toolhead rise with each layer; posed at the finished height.
    nozzle_gap = .068
    gantry_z = bed_z + part_h + nozzle_gap
    gantry = kit.empty('Gantry', (0, 0, gantry_z))
    x0 = -W / 2 + col_w + .005 if mini else -W / 2 + col_w
    x1 = W / 2 - .03 if mini else W / 2 - col_w
    kit.box('XBeam', (x1 - x0, .028, .03), ((x0 + x1) / 2, .02, 0), m['shell'], bevel=.006, parent=gantry)
    kit.cylinder('XRod', .004, x1 - x0, ((x0 + x1) / 2, .004, -.012), m['metal'], axis='X', segments=16, parent=gantry)
    head = toolhead(kit, m, gantry, (bed * .12, -.015, -.012), width=.05, depth=.05, height=.062)
    # Lamp: the A series has no chamber light; a small status light on the toolhead stands in.
    lamp_point = Vector((0, -.2, H))
    return dict(part_h=part_h, top=H, lamp=lamp_point, gantry=gantry, head=head, bed=bed_root,
                travel=(bed_z + nozzle_gap, gantry_z), W=W, D=D, H=H)


# --------------------------------------------------------------------------- studio and output


def studio(kit, frame_target, frame_size):
    scene = kit.scene
    world = bpy.data.worlds.new('BB studio world')
    world.use_nodes = True
    # A soft studio dome: bright overhead, dark below, so glossy panels pick up a gradient.
    nt = world.node_tree
    bg = nt.nodes['Background']
    coord = nt.nodes.new('ShaderNodeTexCoord')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    ramp = nt.nodes.new('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position, ramp.color_ramp.elements[0].color = .35, (.02, .022, .025, 1)
    ramp.color_ramp.elements[1].position, ramp.color_ramp.elements[1].color = .85, (.55, .58, .62, 1)
    remap = nt.nodes.new('ShaderNodeMapRange')
    remap.inputs['From Min'].default_value = -1
    remap.inputs['From Max'].default_value = 1
    nt.links.new(coord.outputs['Generated'], sep.inputs[0])
    nt.links.new(sep.outputs['Z'], remap.inputs['Value'])
    nt.links.new(remap.outputs['Result'], ramp.inputs['Fac'])
    nt.links.new(ramp.outputs['Color'], bg.inputs['Color'])
    bg.inputs[1].default_value = .6
    scene.world = world

    def area(name, loc, power, size, color, target):
        data = bpy.data.lights.new(name, 'AREA')
        data.energy, data.shape, data.size, data.color = power, 'RECTANGLE', size, color
        data.size_y = size * .6
        ob = bpy.data.objects.new(name, data)
        ob.visible_camera = False
        kit.studio.objects.link(ob)
        ob.location = loc
        ob.rotation_euler = (target - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        return ob
    t = frame_target
    s = frame_size
    area('Key', t + Vector((-.9, -1.1, 1.0)) * s, 70 * s * s, 1.2 * s, (1, .97, .93), t)
    area('Fill', t + Vector((1.2, -.8, .3)) * s, 18 * s * s, 1.4 * s, (.85, .9, 1), t)
    area('Rim', t + Vector((.6, 1.3, 1.1)) * s, 90 * s * s, .8 * s, (.8, .88, 1), t)
    area('Top', t + Vector((0, 0, 1.6)) * s, 30 * s * s, 1.0 * s, (1, 1, 1), t)

    # Shadow catcher floor.
    bpy.ops.mesh.primitive_plane_add(size=4)
    floor = bpy.context.active_object
    floor.name = 'Floor'
    for c in floor.users_collection:
        c.objects.unlink(floor)
    kit.studio.objects.link(floor)
    floor.is_shadow_catcher = True

    cam_data = bpy.data.cameras.new('BB camera')
    cam_data.lens = 120
    cam = bpy.data.objects.new('BB camera', cam_data)
    kit.studio.objects.link(cam)
    scene.camera = cam

    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 96
    scene.cycles.use_denoising = True
    try:
        prefs = bpy.context.preferences.addons['cycles'].preferences
        prefs.compute_device_type = 'METAL'
        prefs.get_devices()
        for d in prefs.devices:
            d.use = True
        scene.cycles.device = 'GPU'
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = RES
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGBA'
    scene.render.film_transparent = True
    scene.view_settings.view_transform = 'AgX'
    scene.view_settings.look = 'AgX - Medium High Contrast'
    return cam, floor


def model_bounds(kit):
    pts = []
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for ob in kit.model.objects:
        if ob.type != 'MESH' or ob.hide_render:
            continue
        ev = ob.evaluated_get(depsgraph)
        pts += [ev.matrix_world @ Vector(c) for c in ev.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def frame_camera(kit, cam, yaw=-24, pitch=11, margin=1.08):
    """Aims a long lens at the model from the front left and slightly above, filling the 100 x 120 frame."""
    from bpy_extras.object_utils import world_to_camera_view
    lo, hi = model_bounds(kit)
    centre = (lo + hi) / 2
    direction = Vector((math.sin(math.radians(yaw)), -math.cos(math.radians(yaw)), math.tan(math.radians(pitch)))).normalized()
    distance = 3.0
    cam.location = centre + direction * distance
    cam.rotation_euler = (centre - cam.location).to_track_quat('-Z', 'Y').to_euler()
    cam.data.sensor_fit = 'VERTICAL'
    bpy.context.view_layer.update()
    corners = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
    scene = kit.scene
    # Shrink or grow the focal length until the model fills the frame with a small margin, then centre it.
    for _ in range(4):
        pts = [world_to_camera_view(scene, cam, c) for c in corners]
        xs, ys = [p.x for p in pts], [p.y for p in pts]
        span = max((max(xs) - min(xs)) * RES[0] / RES[1], max(ys) - min(ys))
        cam.data.lens *= 1 / (span * margin)
        bpy.context.view_layer.update()
        pts = [world_to_camera_view(scene, cam, c) for c in corners]
        xs, ys = [p.x for p in pts], [p.y for p in pts]
        cam.data.shift_x = ((min(xs) + max(xs)) / 2 - .5) * RES[1] / RES[0] * 0 + ((min(xs) + max(xs)) / 2 - .5) * (RES[0] / RES[1])
        cam.data.shift_y = ((min(ys) + max(ys)) / 2 - .5)
        bpy.context.view_layer.update()
    return centre


def projected_box(kit, cam, objects):
    from bpy_extras.object_utils import world_to_camera_view
    depsgraph = bpy.context.evaluated_depsgraph_get()
    pts = []
    for ob in objects:
        ev = ob.evaluated_get(depsgraph)
        pts += [world_to_camera_view(kit.scene, cam, ev.matrix_world @ Vector(c)) for c in ev.bound_box]
    xs = [p.x * RES[0] for p in pts]
    ys = [(1 - p.y) * RES[1] for p in pts]
    return dict(x=min(xs), y=min(ys), w=max(xs) - min(xs), h=max(ys) - min(ys))


def set_lamp(kit, on, lamp_point):
    lamp = kit.mats.get('Lamp')
    if lamp:
        lamp.node_tree.nodes['Principled BSDF'].inputs['Emission Strength'].default_value = 12 if on else 0
    light = next((ob for ob in kit.studio.objects if ob.name.startswith('ChamberLight')), None)
    if light is None:
        data = bpy.data.lights.new('ChamberLight', 'POINT')
        data.color = (1, .93, .82)
        data.shadow_soft_size = .03
        light = bpy.data.objects.new('ChamberLight', data)
        # Seen through the door glass it would be a hot spot; it only lights the chamber.
        light.visible_camera = False
        light.visible_transmission = False
        light.visible_glossy = False
        kit.studio.objects.link(light)
        light.location = lamp_point
    light.data.energy = 2.4 if on else 0


def layer_flags(kit, keep, see_through=()):
    """Everything not in `keep` becomes a holdout (cuts the layer) except `see_through`, which is hidden."""
    for ob in kit.model.objects:
        if ob.type != 'MESH':
            continue
        ob.visible_camera = ob not in see_through and ob.name != 'Lamp'
        ob.is_holdout = ob not in keep and ob not in see_through


def reset_flags(kit):
    for ob in kit.model.objects:
        if ob.type == 'MESH':
            ob.visible_camera = ob.name != 'Lamp'
            ob.is_holdout = False


def hide(objects, hidden):
    for ob in objects:
        ob.hide_render = hidden


def sheen_material(kit):
    m = bpy.data.materials.new('BB_Sheen')
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new('ShaderNodeOutputMaterial')
    mix = nt.nodes.new('ShaderNodeMixShader')
    clear = nt.nodes.new('ShaderNodeBsdfTransparent')
    glow = nt.nodes.new('ShaderNodeEmission')
    glow.inputs['Strength'].default_value = .55
    coord = nt.nodes.new('ShaderNodeTexCoord')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    math_add = nt.nodes.new('ShaderNodeMath')
    math_add.operation = 'ADD'
    wave = nt.nodes.new('ShaderNodeValToRGB')
    # A diagonal streak and a softer second one, as on a studio-lit glass door.
    stops = wave.color_ramp.elements
    stops[0].position, stops[0].color = .0, (0, 0, 0, 1)
    stops[1].position, stops[1].color = 1.0, (0, 0, 0, 1)
    for pos, val in ((.26, 0), (.35, .32), (.42, .0), (.52, 0), (.56, .12), (.62, 0)):
        e = stops.new(pos)
        e.color = (val, val, val, 1)
    nt.links.new(coord.outputs['Object'], sep.inputs[0])
    nt.links.new(sep.outputs['X'], math_add.inputs[0])
    nt.links.new(sep.outputs['Z'], math_add.inputs[1])
    remap = nt.nodes.new('ShaderNodeMapRange')
    remap.inputs['From Min'].default_value = -.35
    remap.inputs['From Max'].default_value = .35
    nt.links.new(math_add.outputs[0], remap.inputs['Value'])
    nt.links.new(remap.outputs['Result'], wave.inputs['Fac'])
    nt.links.new(wave.outputs['Color'], mix.inputs['Fac'])
    nt.links.new(clear.outputs[0], mix.inputs[1])
    nt.links.new(glow.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], out.inputs['Surface'])
    return m


def is_in_ams(ob):
    while ob is not None:
        if ob.name.startswith('AMS') and ob.type == 'EMPTY':
            return True
        ob = ob.parent
    return False


def render(kit, path):
    kit.scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True, scene=kit.scene.name)


def export_usdz(kit, path):
    """SceneKit reads UsdPreviewSurface: glass needs opacity instead of transmission."""
    for name, opacity in (('Glass', .16), ('Smoke', .55)):
        mat = kit.mats.get(name)
        if mat:
            mat.node_tree.nodes['Principled BSDF'].inputs['Alpha'].default_value = opacity
    for ob in kit.scene.objects:
        ob.select_set(False)
    for ob in kit.model.objects:
        ob.select_set(True)
    bpy.context.view_layer.objects.active = kit.model.objects[0]
    bpy.ops.wm.usd_export(filepath=str(path), selected_objects_only=True, export_animation=False, export_lights=False,
                          export_cameras=False, export_materials=True, generate_preview_surface=True,
                          generate_materialx_network=False, convert_orientation=True,
                          export_global_forward_selection='NEGATIVE_Z', export_global_up_selection='Y',
                          evaluation_mode='RENDER')
    for name in ('Glass', 'Smoke'):
        mat = kit.mats.get(name)
        if mat:
            mat.node_tree.nodes['Principled BSDF'].inputs['Alpha'].default_value = 1


SHAPES = ('enclosed', 'wide', 'h3c', 'openFrame', 'bedSlinger', 'mini')


def build(shape):
    scene = fresh_scene('Bambu Bar ' + shape)
    kit = Kit(scene, shape)
    m = materials(kit)
    if shape == 'enclosed':
        info = corexy(kit, m, .389, .389, .457)
        ams(kit, m, info['top'])
    elif shape == 'wide':
        info = corexy(kit, m, .492, .485, .55, dual=True, light_shell=False)
        ams(kit, m, info['top'], width=.372, depth=.285, height=.228)
    elif shape == 'h3c':
        info = corexy(kit, m, .493, .571, .587, shell=m['alu'], base_h=.045, screen_top=True, rack=9)
        ams_3h(kit, m, info['top'], .493, .571)
    elif shape == 'openFrame':
        info = corexy(kit, m, .386, .389, .458, open_frame=True)
        ams(kit, m, info['top'])
    elif shape == 'bedSlinger':
        info = bedslinger(kit, m)
        ams_lite(kit, m, info['top'], span=.33)
    else:
        info = bedslinger(kit, m, mini=True)
        ams_lite(kit, m, info['top'], span=.27)
    return kit, m, info


def output(kit, m, info, folder, usdz=True, layers=True):
    folder.mkdir(parents=True, exist_ok=True)
    lo, hi = model_bounds(kit)
    size = max(hi - lo)
    cam, floor = studio(kit, (lo + hi) / 2, size)
    frame_camera(kit, cam)
    shape = kit.shape
    # No floor in the renders: the shadow catcher picks up the dome's occlusion as a dark slab. The app draws a
    # soft contact shadow under the model instead.
    floor.hide_render = True
    meshes = [ob for ob in kit.model.objects if ob.type == 'MESH' and not ob.hide_render]
    anchors = dict(shape=shape, size=list(RES), model=projected_box(kit, cam, meshes))
    if layers:
        spool_objs = [ob for reel, fil, _ in kit.spools for ob in reel + [fil]]
        # Body, light on and off: no print, no spools.
        hide(kit.parts + spool_objs, True)
        for on, name in ((True, 'body'), (False, 'dark')):
            set_lamp(kit, on, info['lamp'])
            render(kit, folder / ('%s-%s.png' % (shape, name)))
        # Without the AMS, for printers that run from the external spool only.
        ams_parts = [ob for ob in kit.model.objects if ob.type == 'MESH' and is_in_ams(ob)]
        hide(ams_parts, True)
        for on, name in ((True, 'bare'), (False, 'baredark')):
            set_lamp(kit, on, info['lamp'])
            render(kit, folder / ('%s-%s.png' % (shape, name)))
        hide(ams_parts, False)
        set_lamp(kit, True, info['lamp'])
        hide(kit.parts + spool_objs, False)
        # The print, cut by whatever stands in front of it (glass excepted).
        layer_flags(kit, set(kit.parts), set(kit.glass))
        hide(spool_objs, True)
        render(kit, folder / ('%s-part.png' % shape))
        anchors['part'] = projected_box(kit, cam, kit.parts)
        hide(spool_objs, False)
        for i, (reel, fil, _) in enumerate(kit.spools):
            layer_flags(kit, set(reel + [fil]), set(kit.glass))
            render(kit, folder / ('%s-spool%d.png' % (shape, i)))
            anchors.setdefault('spools', []).append(projected_box(kit, cam, reel + [fil]))
        # Door reflections.
        if kit.door:
            sheen = sheen_material(kit)
            originals = [(ob, list(ob.data.materials)) for ob in kit.door]
            for ob in kit.door:
                ob.data.materials.clear()
                ob.data.materials.append(sheen)
            for ob in kit.model.objects:
                if ob.type == 'MESH':
                    ob.visible_camera = ob in kit.door
                    ob.is_holdout = False
            render(kit, folder / ('%s-sheen.png' % shape))
            for ob, mats in originals:
                ob.data.materials.clear()
                for mat in mats:
                    ob.data.materials.append(mat)
        reset_flags(kit)
        (folder / ('%s.json' % shape)).write_text(json.dumps(anchors, indent=1))
    if usdz:
        export_usdz(kit, folder / ('%s.usdz' % shape))
    return anchors


def main(args):
    shapes = [a for a in args if a in SHAPES] or list(SHAPES)
    folder = Path(args[args.index('preview') + 1]) if 'preview' in args else OUT
    only_usdz = 'usdz' in args
    for shape in shapes:
        kit, m, info = build(shape)
        if 'build' in args:
            continue
        output(kit, m, info, folder, usdz='nousdz' not in args, layers=not only_usdz)


if __name__ == '__main__':
    main(globals().get('ARGS') or (sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []))
