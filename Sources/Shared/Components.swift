import SwiftUI

/// A filament spool seen from the side: the colour, a hub, and a ring showing how much is left.
struct SpoolSwatch: View {
    var spool: Spool?
    var active = false
    var size: CGFloat = 34
    /// Printed on a wider hub, such as the full spool's weight.
    var hubText: String? = nil

    var body: some View {
        let color = spool.flatMap { $0.isLoaded ? Color(bambuHex: $0.color) : nil }
        ZStack {
            Circle()
                .stroke(Color.primary.opacity(0.10), lineWidth: ringWidth)
            if let remain = spool?.remain, color != nil {
                Circle()
                    .trim(from: 0, to: CGFloat(remain) / 100)
                    .stroke(active ? Palette.accent : Color.primary.opacity(0.55),
                            style: StrokeStyle(lineWidth: ringWidth, lineCap: .round))
                    .rotationEffect(.degrees(-90))
            } else if active {
                Circle().stroke(Palette.accent, lineWidth: ringWidth)
            }
            if let color {
                fill(color)
                    .padding(ringWidth + gap)
            } else {
                Circle()
                    .strokeBorder(Color.primary.opacity(0.22), style: StrokeStyle(lineWidth: 1, dash: [2.5, 2.5]))
                    .padding(ringWidth + gap)
            }
        }
        .frame(width: size, height: size)
    }

    private var ringWidth: CGFloat { max(2, size * 0.075) }
    private var gap: CGFloat { max(1.5, size * 0.06) }

    @ViewBuilder
    private func fill(_ color: Color) -> some View {
        let colors = (spool?.colors ?? []).compactMap { Color(bambuHex: $0) }
        ZStack {
            if colors.count > 1 {
                Circle().fill(AngularGradient(colors: colors + [colors[0]], center: .center))
            } else {
                Circle().fill(color)
            }
            Circle().fill(LinearGradient(colors: [.white.opacity(0.30), .clear, .black.opacity(0.22)],
                                         startPoint: .topLeading, endPoint: .bottomTrailing))
            Circle().strokeBorder(Color.primary.opacity(bambuLuminance(spool?.color ?? "") > 0.85 ? 0.18 : 0.06), lineWidth: 0.75)
            if let hubText {
                Circle()
                    .fill(Color.black.opacity(0.62))
                    .frame(width: size * 0.58, height: size * 0.58)
                    .overlay(Circle().strokeBorder(.white.opacity(0.2), lineWidth: 0.5))
                    .overlay {
                        Text(hubText)
                            .font(.system(size: size * 0.18, weight: .bold, design: .rounded))
                            .foregroundStyle(.white)
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                            .padding(.horizontal, size * 0.03)
                    }
            } else {
                Circle()
                    .fill(Color.black.opacity(0.55))
                    .frame(width: size * 0.2, height: size * 0.2)
                    .overlay(Circle().strokeBorder(.white.opacity(0.18), lineWidth: 0.5))
            }
        }
    }
}

/// A thin progress bar filled with the filament colour, with a soft glow at the leading edge.
struct FilamentProgressBar: View {
    var progress: Double
    var tint: Color
    var height: CGFloat = 8
    var glow = true

    var body: some View {
        GeometryReader { geo in
            let w = max(height, geo.size.width * CGFloat(min(1, max(0, progress))))
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.09))
                Capsule()
                    .fill(LinearGradient(colors: [tint.opacity(0.75), tint], startPoint: .leading, endPoint: .trailing))
                    .frame(width: progress > 0 ? w : 0)
                    .overlay(alignment: .top) {
                        Capsule().fill(.white.opacity(0.28)).frame(height: height * 0.35).padding(.horizontal, height * 0.4).padding(.top, 1)
                            .frame(width: progress > 0 ? w : 0)
                    }
                    .shadow(color: glow ? tint.opacity(0.45) : .clear, radius: height * 0.6)
            }
        }
        .frame(height: height)
    }
}

/// A circular gauge for a single percentage, used by the small widget and the window's hero.
struct ProgressRing: View {
    var progress: Double
    var tint: Color
    var lineWidth: CGFloat = 8
    var glow = true

    var body: some View {
        ZStack {
            Circle().stroke(Color.primary.opacity(0.10), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: CGFloat(min(1, max(0, progress))))
                .stroke(AngularGradient(colors: [tint.opacity(0.55), tint], center: .center,
                                        startAngle: .degrees(0), endAngle: .degrees(360 * max(0.01, progress))),
                        style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .shadow(color: glow ? tint.opacity(0.35) : .clear, radius: lineWidth * 0.5)
        }
    }
}

/// "● Printing" capsule.
struct StatePill: View {
    var state: JobState
    var text: String?
    var compact = false
    var color: Color?

    var body: some View {
        let color = color ?? Palette.color(for: state)
        HStack(spacing: 5) {
            Circle().fill(color).frame(width: 6, height: 6)
                .shadow(color: color.opacity(0.8), radius: 3)
            Text(text ?? state.title)
                .font(.system(size: compact ? 10.5 : 11.5, weight: .semibold))
                .foregroundStyle(color)
                .lineLimit(1)
        }
        .padding(.horizontal, compact ? 7 : 9)
        .padding(.vertical, compact ? 3 : 4)
        .background(Capsule().fill(color.opacity(0.14)))
    }
}
