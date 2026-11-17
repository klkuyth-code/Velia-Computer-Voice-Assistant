import type { ProviderState } from '../lib/presentation';
import { presentProviderState } from '../lib/presentation';

export function StatusBadge({ state }: { readonly state: ProviderState }) {
  const presentation = presentProviderState(state);
  return (
    <span className={`status-badge status-badge--${presentation.tone}`}>
      <span className="status-badge__dot" />
      {presentation.label}
    </span>
  );
}
