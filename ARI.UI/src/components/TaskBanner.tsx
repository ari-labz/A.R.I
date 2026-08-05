import { useState, useEffect } from "react"

interface Props {
    tasks: Set<string>
    onCancel: (name: string) => void
}

export default function TaskBanner({ tasks, onCancel }: Props) {
    const [cancelling, setCancelling] = useState<string | null>(null)

    useEffect(() => {
        if (cancelling && !tasks.has(cancelling)) setCancelling(null)
    }, [tasks, cancelling])

    if (tasks.size === 0) return null

    const names = [...tasks]

    function handleCancel(name: string) {
        setCancelling(name)
        onCancel(name)
    }

    return (
        <div id="task-banner">
            <span className="task-banner-icon">⚠</span>
            <span className="task-banner-text">
                {names.length === 1
                    ? <><strong>{names[0]}</strong> is running — responses may be slower</>
                    : <><strong>{names.join(", ")}</strong> are running — responses may be slower</>
                }
            </span>
            <span className="task-banner-actions">
                {names.map(name => (
                    <button
                        key={name}
                        className="task-banner-cancel"
                        disabled={cancelling === name}
                        onClick={() => handleCancel(name)}
                    >
                        {cancelling === name ? "Cancelling…" : `Cancel ${name}`}
                    </button>
                ))}
            </span>
        </div>
    )
}
