"""Build-time warmup: loads the engine once so PaddleOCR downloads its models into the image, then reads a test line."""
from PIL import Image, ImageDraw

from app import load_engine, read_lines

image = Image.new("RGB", (320, 64), "white")
ImageDraw.Draw(image).text((10, 20), "pH 7.82  28.6 C", fill="black")
print("warmup read:", read_lines(load_engine(), image))
