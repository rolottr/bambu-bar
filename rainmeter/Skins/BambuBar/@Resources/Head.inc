; What every size draws first: the script, the animation timer, the shadow, the card and the content container.
; Included from [Variables] of Small/Medium/Large.ini, so these sections come before the size's own meters.

[MeasureScript]
Measure=Script
ScriptFile=#@#Scripts\widget.lua
Size=#Size#

; Animation clock. Built into Rainmeter 5.0 ("Measure=ActionTimer", https://www.rainmeter.net/changes-5.0/);
; the script runs it with !CommandMeasure MeasureAnimation "Execute N" / "Stop N".
; 1: one frame every 16 ms while something moves (fades, tweens, pulse, art crossfade, hover).
; 2: the connecting spinner, every 50 ms (about 20 fps).
; Repeat is expanded into a list, so the counts stay short; the script restarts a list that runs out.
[MeasureAnimation]
Measure=ActionTimer
ActionList1=Repeat Frame, 16, 150
Frame=[!CommandMeasure MeasureScript "Frame()"]
ActionList2=Repeat Spin, 50, 200
Spin=[!CommandMeasure MeasureScript "Spin()"]
IgnoreWarnings=1

; The soft shadow, pre-rendered for each size (generated once; see ..\README.md).
[Shadow]
Meter=Image
ImageName=#@#Images\shadow-#Size#.png
X=0
Y=0
W=(#CardW#+2*#Margin#)
H=(#CardH#+2*#Margin#)

; The card (always dark, ledger O6): gradient #292B2E to #141719, 14 px corners, white 8 % border. Hover lightens
; it and takes the border to 22 % (the script tweens it over 140 ms). Left click opens the printer in the app;
; the script writes the serial into LeftMouseUpAction.
[Card]
Meter=Shape
X=#Margin#
Y=#Margin#
AntiAlias=1
Shape=Rectangle 0,0,#CardW#,#CardH#,14 | Fill LinearGradient CardGradient | StrokeWidth 0
Shape2=Rectangle 0.5,0.5,(#CardW#-1),(#CardH#-1),13.5 | Fill Color 00000000 | StrokeWidth 1 | Stroke Color FFFFFF14
CardGradient=90 | 292B2E ; 0 | 141719 ; 1
MouseOverAction=[!CommandMeasure MeasureScript "Hover(1)"]
MouseLeaveAction=[!CommandMeasure MeasureScript "Hover(0)"]
MouseActionCursorName=Hand
LeftMouseUpAction=["bambubar://printer/"]

; Every content meter has Container=Content: drawn through this shape, so its alpha fades them all (150 ms out,
; 180 ms in on a state change). Positions inside are card coordinates.
[Content]
Meter=Shape
X=#Margin#
Y=#Margin#
AntiAlias=1
Shape=Rectangle 0,0,#CardW#,#CardH#,14 | Fill Color FFFFFFFF | StrokeWidth 0
