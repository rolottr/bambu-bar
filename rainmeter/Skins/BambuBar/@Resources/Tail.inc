; What every size draws last. Included from the [End] section of Small/Medium/Large.ini.

; The finish pulse: one ring out of the dial (Small) or the status dot (Medium, Large), 700 ms. Window coordinates.
[Pulse]
Meter=Shape
X=0
Y=0
AntiAlias=1
Hidden=1
Shape=Ellipse 0,0,1 | Fill Color 00000000 | StrokeWidth 0

; Text probes: the script sets a text and font here, updates the meter and reads its size (one line, and wrapped
; to W), to shrink big numbers, wrap reasons and stack lines. Transparent, never seen.
[ProbeLine]
Meter=String
X=0
Y=0
AntiAlias=1
FontColor=00000000
Text=Ag

[ProbeWrap]
Meter=String
X=0
Y=0
W=100
ClipString=2
AntiAlias=1
FontColor=00000000
Text=Ag
