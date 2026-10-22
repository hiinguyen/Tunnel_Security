from radar_mapping import MAX_TARGETS, to_radar_tuples


def test_only_confirmed_targets_are_sent_with_range_in_metres(radar_sample):
    tuples = to_radar_tuples(radar_sample)

    assert len(tuples) == sum(1 for t in radar_sample if t.get("confirmed"))
    x, y, z, vel, rng, fa = tuples[0]
    assert (x, y, z) == (1.0, 5.0, 0.2)
    assert rng == 5.1
    assert vel == -0.5
    assert fa == 2.0


def test_heartbeat_frames_and_overflow_are_safe():
    assert to_radar_tuples(None) == []
    many = [{"x": 0, "y": 1, "z": 0, "speed": 0, "range_mm": 1000, "confirmed": True}] * 100
    assert len(to_radar_tuples(many)) == MAX_TARGETS
