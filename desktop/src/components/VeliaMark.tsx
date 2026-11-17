export function VeliaMark({ compact = false }: { readonly compact?: boolean }) {
  return (
    <div className={`velia-mark${compact ? ' velia-mark--compact' : ''}`} aria-hidden="true">
      <span className="velia-mark__ring velia-mark__ring--outer" />
      <span className="velia-mark__ring velia-mark__ring--inner" />
      <span className="velia-mark__core">V</span>
    </div>
  );
}
