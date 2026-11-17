import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { AssistantOverlay } from './components/AssistantOverlay';
import './styles.css';
import './appShell.css';

const root = document.getElementById('root');
if (!root) {
  throw new Error('Velia renderer root is missing.');
}

const assistantView = new URLSearchParams(window.location.search).get('view') === 'assistant';
document.documentElement.classList.toggle('assistant-view', assistantView);

createRoot(root).render(
  <StrictMode>
    {assistantView ? <AssistantOverlay /> : <App />}
  </StrictMode>,
);
