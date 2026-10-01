"""Dev-only: builds small ONNX models + onnxruntime reference outputs to validate the managed C# runtime."""
import numpy as np, torch, torch.nn as nn, onnxruntime as ort, sys, os
out = sys.argv[1]; os.makedirs(out, exist_ok=True)
torch.manual_seed(0)

class Block(nn.Module):
    """Transformer-ish block with the ops UniMate-style nets use."""
    def __init__(s, d=64, h=4):
        super().__init__()
        s.enc = nn.TransformerEncoderLayer(d, h, 128, dropout=0.0, activation="gelu", batch_first=True, norm_first=True)
        s.ada = nn.Linear(16, 2 * d)
        s.out = nn.Linear(d, 12)
    def forward(s, x, cond, mask):
        scale, shift = s.ada(torch.nn.functional.silu(cond)).chunk(2, dim=-1)
        x = x * (1 + scale[:, None]) + shift[:, None]
        x = s.enc(x, src_key_padding_mask=mask)
        y = torch.cat([x.mean(1, keepdim=True), x[:, 1:3]], 1)
        y = torch.where(y > 0, y, 0.1 * y)
        return s.out(y).softmax(-1) + torch.sin(y[..., :12]) * torch.rsqrt(y.pow(2).mean(-1, keepdim=True) + 1e-6)

m = Block().eval()
x = torch.randn(2, 10, 64); cond = torch.randn(2, 16); mask = torch.zeros(2, 10, dtype=torch.bool); mask[1, 7:] = True
path = os.path.join(out, "block.onnx")
torch.onnx.export(m, (x, cond, mask), path, input_names=["x", "cond", "mask"], output_names=["y"], opset_version=17, dynamo=False)
sess = ort.InferenceSession(path)
y = sess.run(None, {"x": x.numpy(), "cond": cond.numpy(), "mask": mask.numpy()})[0]
with torch.no_grad(): yt = m(x, cond, mask).numpy()
print("ort vs torch", np.abs(y - yt).max())
np.save(os.path.join(out, "block_x.npy"), x.numpy()); np.save(os.path.join(out, "block_cond.npy"), cond.numpy())
np.save(os.path.join(out, "block_mask.npy"), mask.numpy().astype(np.float32)); np.save(os.path.join(out, "block_y.npy"), y)
import onnx; ops = sorted({n.op_type for n in onnx.load(path).graph.node}); print("ops", ops)
