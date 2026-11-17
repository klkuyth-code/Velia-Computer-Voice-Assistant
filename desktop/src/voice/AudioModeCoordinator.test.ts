import { describe, expect, it } from 'vitest';
import { AudioModeCoordinator } from './AudioModeCoordinator';

describe('AudioModeCoordinator', () => {
  it('allows only one microphone mode at a time', () => {
    const coordinator = new AudioModeCoordinator();
    expect(coordinator.acquire('pushToTalk')).toBe(true);
    expect(coordinator.acquire('live')).toBe(false);
    coordinator.release('live');
    expect(coordinator.owner).toBe('pushToTalk');
    coordinator.release('pushToTalk');
    expect(coordinator.acquire('live')).toBe(true);
  });

  it('keeps ordinary long conversation mutually exclusive with Live and push-to-talk', () => {
    const coordinator = new AudioModeCoordinator();
    expect(coordinator.acquire('longConversation')).toBe(true);
    expect(coordinator.acquire('pushToTalk')).toBe(false);
    expect(coordinator.acquire('live')).toBe(false);
    coordinator.release('longConversation');
    expect(coordinator.acquire('pushToTalk')).toBe(true);
  });

  it('lets an explicit foreground recording preempt passive wake monitoring', () => {
    const coordinator = new AudioModeCoordinator();
    const owners: Array<string | undefined> = [];
    coordinator.subscribe((owner) => owners.push(owner));
    expect(coordinator.acquire('wake')).toBe(true);
    expect(coordinator.acquire('wakeCommand')).toBe(true);
    expect(coordinator.owner).toBe('wakeCommand');
    coordinator.release('wakeCommand');
    expect(owners).toEqual(['wake', 'wakeCommand', undefined]);
  });

  it('keeps local barge-in capture mutually exclusive with Live', () => {
    const coordinator = new AudioModeCoordinator();
    expect(coordinator.acquire('bargeIn')).toBe(true);
    expect(coordinator.acquire('live')).toBe(false);
    coordinator.release('bargeIn');
    expect(coordinator.acquire('live')).toBe(true);
  });
});
