export const SECTION_CATEGORIES = [
  { label: '', ids: ['home'] },
  { label: 'System', ids: ['challenge', 'architecture', 'rules'] },
  { label: 'Operate', ids: ['getting-started', 'performance', 'ci-cd-pipeline'] },
] as const;

export const SECTION_ORDER = SECTION_CATEGORIES.flatMap(({ ids }) => ids);

export const DOC_META: Record<string, { label: string; intent: string; signal: string }> = {
  home: {
    label: 'Home',
    intent: 'Start with the project map, runtime stance, and evidence links.',
    signal: 'orientation',
  },
  challenge: {
    label: 'Challenge',
    intent: 'Understand the Rinha workload, scoring pressure, and resource envelope.',
    signal: 'ruleset',
  },
  architecture: {
    label: 'Architecture',
    intent: 'Trace traffic from k6 to yolo load balancer to UDS NativeAOT APIs.',
    signal: 'runtime path',
  },
  rules: {
    label: 'Rules',
    intent: 'Check submission shape, limits, image constraints, and correctness gates.',
    signal: 'submission',
  },
  'getting-started': {
    label: 'Getting Started',
    intent: 'Build, run, and smoke-test the stack with the documented commands.',
    signal: 'operator entry',
  },
  performance: {
    label: 'Performance',
    intent: 'Read the latency tactics, scoring tradeoffs, and benchmark caveats.',
    signal: 'hot path',
  },
  'ci-cd-pipeline': {
    label: 'CI/CD Pipeline',
    intent: 'Follow image builds, benchmark archival, Pages deploy, and provenance.',
    signal: 'automation',
  },
};
