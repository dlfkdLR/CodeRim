import SwiftUI

/// First run: what CodeRim does, three steps, and one button to scan the computer's QR code.
struct MobileWelcomeView: View {
    @ObservedObject var model: MobileAppModel
    @State private var scanning = false

    var body: some View {
        ScrollView {
            VStack(spacing: 24) {
                VStack(spacing: 14) {
                    CodeRimSetupMark()
                        .frame(width: 64, height: 64)
                        .background { Circle().fill(islandGreen.opacity(0.35)).blur(radius: 28).scaleEffect(1.5) }
                        .accessibilityHidden(true)
                    Text("Your work,\na glance away.")
                        .font(.system(.largeTitle, design: .rounded, weight: .bold))
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                    Text("Usage and live tasks from your computers, right in your Island.")
                        .font(.body).foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.top, 4)
                SetupIslandPreview()
                SetupSteps()
                if let error = model.errorMessage {
                    Label(error, systemImage: "exclamationmark.circle.fill")
                        .font(.footnote).foregroundStyle(.red)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(14)
                        .background(Color.red.opacity(0.1), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.horizontal, 22).padding(.bottom, 24)
        }
        .scrollBounceBehavior(.basedOnSize)
        .background {
            LinearGradient(colors: [islandGreen.opacity(0.14), Color(.systemGroupedBackground)],
                           startPoint: .top, endPoint: UnitPoint(x: 0.5, y: 0.45))
                .ignoresSafeArea()
        }
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 12) {
                Button { scanning = true } label: {
                    HStack(spacing: 10) {
                        if model.busy { ProgressView().tint(Color(.systemBackground)) }
                        else { Image(systemName: "qrcode.viewfinder") }
                        Text(model.busy ? "Connecting…" : "Scan QR code")
                    }
                    .font(.headline)
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(PrimaryCapsuleButtonStyle())
                .disabled(model.busy)
                .accessibilityLabel("Scan the QR code on your computer")
                Label("No sign-in. Your AI accounts stay on your computers.", systemImage: "lock.fill")
                    .font(.footnote).foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
            .padding(.horizontal, 22).padding(.top, 14).padding(.bottom, 8)
            .background(.bar)
        }
        .toolbar(.hidden, for: .navigationBar)
        .sheet(isPresented: $scanning) { MobileQRScanner { link in Task { await model.connect(link) } } }
    }
}

/// A slim filled capsule; `.borderedProminent` adds its own padding and reads too heavy here.
struct PrimaryCapsuleButtonStyle: ButtonStyle {
    var height: CGFloat = 50
    @Environment(\.isEnabled) private var isEnabled
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .foregroundStyle(Color(.systemBackground))
            .frame(height: height)
            .background(Color.primary.opacity(isEnabled ? 1 : 0.4), in: Capsule())
            .opacity(configuration.isPressed ? 0.75 : 1)
            .scaleEffect(configuration.isPressed ? 0.98 : 1)
            .animation(.easeOut(duration: 0.15), value: configuration.isPressed)
    }
}

/// Three short steps instead of one paragraph of directions.
private struct SetupSteps: View {
    private let steps: [(String, String)] = [
        ("Open CodeRim on your computer", "Settings → iPhone, on a Mac or Windows PC"),
        ("Choose Connect iPhone", "A QR code appears for five minutes."),
        ("Scan it with this iPhone", "The server is in the code. Nothing to type."),
    ]
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(steps.enumerated()), id: \.offset) { index, step in
                HStack(alignment: .top, spacing: 14) {
                    Text("\(index + 1)")
                        .font(.system(.subheadline, design: .rounded, weight: .bold))
                        .frame(width: 28, height: 28)
                        .background(Color(.tertiarySystemFill), in: Circle())
                    VStack(alignment: .leading, spacing: 3) {
                        Text(step.0).font(.subheadline.weight(.semibold))
                        Text(step.1).font(.footnote).foregroundStyle(.secondary)
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 12)
                .accessibilityElement(children: .combine)
                if index < steps.count - 1 { Divider().padding(.leading, 42) }
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 4)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
    }
}

/// An explicitly labelled illustration, never connected to live state or navigation.
struct SetupIslandPreview: View {
    @State private var progress = 0.0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 16) {
                ZStack {
                    Circle().strokeBorder(Color(white: 0.19), lineWidth: 6)
                    Circle().inset(by: 3).trim(from: 0, to: progress)
                        .stroke(islandGreen, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    IslandProviderMark(id: "codex", name: "Codex").frame(width: 21, height: 21).foregroundStyle(.white)
                }.frame(width: 50, height: 50)
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 6) {
                        Text("Codex").font(.caption.weight(.semibold)).foregroundStyle(.white.opacity(0.65))
                        LiveDot(color: islandGreen)
                    }
                    Text("Making progress").font(.subheadline.weight(.medium)).foregroundStyle(.white)
                    Text("68% left  ·  284K tokens today").font(.caption2).foregroundStyle(.white.opacity(0.65))
                }.fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }.padding(22).frame(maxWidth: .infinity, alignment: .leading)
                .background(.black, in: RoundedRectangle(cornerRadius: 32, style: .continuous))
                // Keeps the black preview distinct from a black background in Dark Mode.
                .overlay(RoundedRectangle(cornerRadius: 32, style: .continuous).strokeBorder(.white.opacity(0.12)))
                .shadow(color: .black.opacity(0.18), radius: 18, y: 10)
                .accessibilityHidden(true)
            Text("Island preview · Sample data").font(.caption2).foregroundStyle(.secondary)
        }.accessibilityElement(children: .ignore)
            .accessibilityLabel("Sample Island preview. Usage and live tasks appear here after setup.")
            .onAppear {
                guard !reduceMotion else { progress = 0.68; return }
                withAnimation(.spring(duration: 1.4).delay(0.25)) { progress = 0.68 }
            }
    }
}
