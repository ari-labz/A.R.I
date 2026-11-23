/// The state icons from issue 200: eye = reading, brain = thinking, speech bubble = typing. They are the exact artwork from
/// the issue, used as masks so they take their colour from the text (sidebar, effort button, memory card).
type IconProps = { size?: number; className?: string }

function MaskIcon({ name, size, className }: IconProps & { name: string }) {
    const url = `url(/images/state-${name}.png)`
    return (
        <span
            className={`state-icon ${className ?? ""}`}
            aria-hidden="true"
            style={{ width: size, height: size, WebkitMaskImage: url, maskImage: url }}
        />
    )
}

export const EyeIcon    = ({ size = 16, className }: IconProps) => <MaskIcon name="eye"    size={size} className={className} />
export const BrainIcon  = ({ size = 16, className }: IconProps) => <MaskIcon name="brain"  size={size} className={className} />
export const SpeechIcon = ({ size = 16, className }: IconProps) => <MaskIcon name="speech" size={size} className={className} />
