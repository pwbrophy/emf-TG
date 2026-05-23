Robot X is shooting and Robot Y

Assuming ping is 20ms

| server                                                       | robot "x"                                                    | enemy robots "y"                                             | ping | cum. |
| ------------------------------------------------------------ | ------------------------------------------------------------ | ------------------------------------------------------------ | ---- | ---- |
|                                                              | server: I want to shoot                                      |                                                              | 20   | 20   |
| x: check queue, ok you can shoot<br />y: turn on your IR receivers |                                                              |                                                              | 20   | 40   |
|                                                              | fire left IR led continuously<br />server: I've started firing left LED | server: my IR receivers are on                               | 20   | 60   |
| server waits for both X to begin firing and Y to turn on recievers, then sends message to Y: start timer |                                                              |                                                              | 20   | 80   |
|                                                              |                                                              | timer: receive IR for 20ms <br />then message server: reception phase complete, report hits | 20   | 100  |
| server waits for Y reception phase, then<br />X: switch to right IR, begin shooting<br />y: turn on your IR receivers |                                                              |                                                              | 20   | 120  |
|                                                              | fire right IR led continuously<br />server: I've started firing right LED | server: my IR receivers are on                               | 20   | 120  |
| server waits for both X to begin firing and Y to turn on recievers, then sends message to Y: start timer |                                                              |                                                              | 20   | 140  |
|                                                              |                                                              | timer: receive IR for 20ms <br />then message server: reception phase complete, report hits | 20   | 160  |
| server messages robot x: stop firing                         |                                                              |                                                              | 20   | 180  |
|                                                              | turn off IR LEDs                                             |                                                              | 20   | 200  |
|                                                              |                                                              |                                                              |      |      |
|                                                              |                                                              |                                                              |      |      |

